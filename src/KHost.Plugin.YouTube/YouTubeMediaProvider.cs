using KHost.Plugins.Sdk.Models;
using KHost.Plugins.Sdk.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KHost.Plugin.YouTube;

public class YouTubeMediaProvider : IMediaProvider
{
    private const int MaxAllowedResults = 50;

    private readonly IPluginContext _plugin;
    private readonly YouTubeSettings _settings;
    private readonly YtDlpRunner _run;

    // The provider is a singleton and a host on slow venue internet will click Enqueue twice
    // before the first download finishes; this stops the second click from starting a duplicate
    // yt-dlp process or a duplicate queue entry. BeginImportAsync's own idempotency only protects
    // the DB row, not the in-flight process.
    private readonly ConcurrentDictionary<string, byte> _downloadsInFlight = new();

    public YouTubeMediaProvider(IPluginContext plugin)
        : this(plugin, BuildRunner(plugin))
    {
    }

    public YouTubeMediaProvider(IPluginContext plugin, YtDlpRunner run)
    {
        _plugin = plugin;
        _settings = plugin.BindSettings<YouTubeSettings>();
        _run = run;

        Actions = [
            new() {
                DisplayName = "Enqueue",
                Description = "Downloads the video into the library, then enqueues it for the selected singer",
                Icon = "plus-lg",
                PerformAsync = DownloadAndEnqueueAsync,
                SubActions = [
                    new() {
                        DisplayName = "Open on YouTube",
                        Description = "Open on YouTube",
                        Icon = "youtube",
                        PerformAsync = OpenInBrowserAsync,
                    }
                ],
            }
        ];
    }

    public string DisplayName => "YouTube";

    public string SourceName => "YouTube";

    public IEnumerable<MediaProviderAction> Actions { get; }

    public async Task<List<MediaSearchEntity>> SearchAsync(string query, int pageNumber = 0, int pageSize = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // ytsearch takes a count, not an offset, so there is no page to serve but the first.
        if (pageNumber > 1)
            return [];

        var count = Math.Clamp(pageSize > 0 ? pageSize : _settings.MaxResults, 1, MaxAllowedResults);

        // --flat-playlist keeps this to the search response itself. Without it yt-dlp resolves every
        // hit in turn, which is a page load per row.
        var output = await _run(
            [
                $"ytsearch{count.ToString(CultureInfo.InvariantCulture)}:{query} Karaoke",
                "--dump-json",
                "--flat-playlist",
                "--no-warnings",
            ],
            CancellationToken.None);

        return [.. ParseResults(output)];
    }

    /// <summary>One JSON object per line. A blank or half-written line is skipped, not thrown over.</summary>
    private IEnumerable<MediaSearchEntity> ParseResults(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            MediaSearchEntity entity;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                // Without an id there is nothing to enqueue or open later, so the row is no use.
                if (!root.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } videoId)
                    continue;

                var rawTitle = root.TryGetProperty("title", out var title) ? title.GetString() ?? videoId : videoId;
                var channelName = root.TryGetProperty("channel", out var channel) ? channel.GetString() ?? "" : "";
                var (parsedTitle, artist) = YouTubeTitleParser.Parse(rawTitle, channelName);

                entity = new MediaSearchEntity
                {
                    Title = parsedTitle,
                    Artist = artist,
                    SourceDisplayName = DisplayName,
                    Source = SourceName,
                    ForeignKey = videoId,
                    Duration = ReadDuration(root),
                    // The parse can be wrong; keeping the real video title lets a host see what it
                    // actually came from instead of just trusting the guess.
                    Notes = BuildNotes(channelName, rawTitle, parsedTitle),
                    SupportedActions = Actions,
                };
            }
            catch (JsonException)
            {
                continue;
            }

            yield return entity;
        }
    }

    /// <summary>Channel name, plus the raw video title whenever the parse changed it.</summary>
    private static string BuildNotes(string channelName, string rawTitle, string parsedTitle)
        => parsedTitle == rawTitle
            ? channelName
            : channelName.Length > 0 ? $"{channelName} — “{rawTitle}”" : $"“{rawTitle}”";

    /// <summary>Seconds, and null for anything without a real one — a live stream reports none.</summary>
    private static TimeSpan? ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out var duration)) return null;

        return duration.ValueKind is JsonValueKind.Number
            && duration.TryGetDouble(out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    private static YtDlpRunner BuildRunner(IPluginContext plugin)
    {
        var settings = plugin.BindSettings<YouTubeSettings>();

        var resolver = new YtDlpResolver(
            settings.YtDlpPath,
            Path.Combine(AppContext.BaseDirectory, "cache", "tools"));

        return new YtDlp(resolver).RunAsync;
    }

    private Task OpenInBrowserAsync(MediaSearchEntity entity)
    {
        // KHost runs on the host's own machine, so the default browser is the right target.
        Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={entity.ForeignKey}") { UseShellExecute = true });

        return Task.CompletedTask;
    }

    private async Task DownloadAndEnqueueAsync(MediaSearchEntity entity)
    {
        // Read per call: the host can hot-reload MediaDirectory, so a cached value could go stale.
        var directory = Path.Combine(_plugin.Library.MediaDirectory, "youtube");
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, $"{entity.ForeignKey}.mp4");
        var request = new MediaImportRequest
        {
            FilePath = destination,
            Title = entity.Title,
            Artist = entity.Artist,
            Duration = entity.Duration,
            Notes = entity.Notes,
        };

        // A re-click must not re-fetch a video already sitting in the library's cache.
        if (File.Exists(destination))
        {
            var readyId = await _plugin.Library.ImportAsync(request);
            await _plugin.Library.EnqueueAsync(readyId);
            return;
        }

        if (!_downloadsInFlight.TryAdd(entity.ForeignKey, 0))
            return;

        try
        {
            // Enqueue immediately, before the download runs, so the singer's queue shows the
            // Downloading spinner row the moment the host clicks — not minutes later on slow
            // venue internet.
            var ticket = await _plugin.Library.BeginImportAsync(request);
            await _plugin.Library.EnqueueAsync(ticket.MediaId);

            string output;
            try
            {
                output = await _run(
                    [
                        $"https://www.youtube.com/watch?v={entity.ForeignKey}",
                        "-f",
                        "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b",
                        "--merge-output-format",
                        "mp4",
                        "-o",
                        destination,
                        "--no-warnings",
                    ],
                    ticket.Cancellation);
            }
            catch (OperationCanceledException)
            {
                await CleanUpAfterCancelAsync(directory, entity.ForeignKey, destination, ticket.MediaId);
                throw;
            }
            catch
            {
                await _plugin.Library.FailImportAsync(ticket.MediaId);
                throw;
            }

            if (!File.Exists(destination))
            {
                await _plugin.Library.FailImportAsync(ticket.MediaId);
                throw new InvalidOperationException($"yt-dlp did not produce '{destination}': {output}");
            }

            await _plugin.Library.CompleteImportAsync(ticket.MediaId);
        }
        finally
        {
            _downloadsInFlight.TryRemove(entity.ForeignKey, out _);
        }
    }

    /// <summary>
    /// A cancelled download can leave the destination plus yt-dlp's own intermediates (.part,
    /// .ytdl) and, in bv+ba mode, per-stream fragment files — all named "{foreignKey}.*" in the
    /// plugin-owned directory, so a prefix sweep catches every one of them in one pass.
    /// EnumerateFileSystemEntries (not EnumerateFiles) so a same-named directory is attempted too
    /// rather than silently skipped. Only once nothing survives at the destination path is the row
    /// safe to discard — Path.Exists, not File.Exists, so a leftover directory still routes to
    /// FailImportAsync instead of being reported as gone.
    /// </summary>
    private async Task CleanUpAfterCancelAsync(string directory, string foreignKey, string destination, Guid mediaId)
    {
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, $"{foreignKey}.*"))
            {
                try { File.Delete(path); }
                catch { /* best effort; a survivor routes to FailImportAsync below */ }
            }
        }

        if (Path.Exists(destination))
            await _plugin.Library.FailImportAsync(mediaId);
        else
            await _plugin.Library.DiscardImportAsync(mediaId);
    }
}
