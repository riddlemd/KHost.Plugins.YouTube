using KHost.Abstractions.Exceptions;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KHost.Plugins.YouTube;

public class YouTubeMediaProvider : IMediaProvider
{
    private const int MaxAllowedResults = 50;

    /// <summary>A flat search is one request and a normal one takes seconds. yt-dlp's own socket
    /// timeout and retries can add up to a minute on a bad link, so this waits that out and no more:
    /// the host is staring at a spinner.</summary>
    internal static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(90);

    private readonly IPluginContext _plugin;
    private readonly IMediaAcquisitionService _media;
    private readonly ISingerQueueService _queue;
    private readonly IPerformanceService _performances;
    private readonly ILogger<YouTubeMediaProvider> _logger;
    private readonly IFlashService _flash;
    private readonly YouTubeSettings _settings;
    private readonly YtDlpRunner _run;

    // A host on slow venue internet can click Enqueue twice before the first download finishes;
    // this stops a duplicate yt-dlp process, which BeginImportAsync's DB idempotency does not.
    private readonly ConcurrentDictionary<string, byte> _downloadsInFlight = new();

    // What SearchAsync last flashed, so a search that keeps failing the same way (every keystroke
    // on slow typing) does not restack the same message; null once a search succeeds again.
    private string? _lastSearchFailureFlash;

    // Every parameter past the context comes from the host's own container: the loader builds
    // providers with ActivatorUtilities, so there is no facade to go through for them.
    public YouTubeMediaProvider(
        IPluginContext plugin,
        IMediaAcquisitionService media,
        ISingerQueueService queue,
        IPerformanceService performances,
        ILogger<YouTubeMediaProvider> logger,
        IFlashService flash)
        : this(plugin, media, queue, performances, logger, flash, BuildRunner(plugin))
    {
    }

    public YouTubeMediaProvider(
        IPluginContext plugin,
        IMediaAcquisitionService media,
        ISingerQueueService queue,
        IPerformanceService performances,
        ILogger<YouTubeMediaProvider> logger,
        IFlashService flash,
        YtDlpRunner run)
    {
        _plugin = plugin;
        _media = media;
        _queue = queue;
        _performances = performances;
        _logger = logger;
        _flash = flash;
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

    private const string ThumbnailKey = "thumbnail";
    private const string PublisherKey = "publisher";

    /// <summary>Not a column: no <see cref="MediaResultColumn"/> names it, so nothing renders it.
    /// It rides on the row so import can write the parsed title while the list shows the raw one.</summary>
    private const string CleanTitleKey = "cleanTitle";

    /// <summary>YouTube's own verified tick, which its karaoke channels of any size carry.</summary>
    private const string VerifiedMark = " \u2713";

    public string DisplayName => "YouTube";

    public string SourceName => "YouTube";

    public IEnumerable<MediaProviderAction> Actions { get; }

    /// <summary>What a host picks a track on. Artist is deliberately absent: it is parsed from
    /// the title and can be wrong, while the channel says whether this is a real karaoke upload.</summary>
    public IReadOnlyList<MediaResultColumn> Columns =>
    [
        new() { Key = ThumbnailKey, Header = "", Kind = MediaResultColumnKind.Thumbnail, Essential = false },
        new() { Key = MediaResultColumn.TitleKey, Header = "Title" },
        new() { Key = PublisherKey, Header = "Published by", Essential = false },
        new() { Key = MediaResultColumn.DurationKey, Header = "Duration" },
    ];

    public async Task<List<MediaSearchEntity>> SearchAsync(string query, int pageNumber = 0, int pageSize = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // ytsearch takes a count, not an offset, so there is no page to serve but the first.
        if (pageNumber > 1)
            return [];

        var count = Math.Clamp(pageSize > 0 ? pageSize : _settings.MaxResults, 1, MaxAllowedResults);

        string output;

        try
        {
            // --flat-playlist keeps this to the search response itself. Without it yt-dlp resolves
            // every hit in turn, which is a page load per row.
            output = await _run(
                [
                    $"ytsearch{count.ToString(CultureInfo.InvariantCulture)}:{query} Karaoke",
                    "--dump-json",
                    "--flat-playlist",
                    "--no-warnings",
                ],
                CancellationToken.None,
                timeout: SearchTimeout);
        }
        catch (Exception ex)
        {
            // The host already logs a thrown SearchAsync and treats it as no results (see
            // IMediaProvider.SearchAsync), so without this the operator just sees an empty list
            // with nothing explaining why.
            _logger.LogWarning(ex, "YouTube search failed for '{Query}'", query);
            FlashSearchFailureOnce(DescribeSearchFailure(ex));

            return [];
        }

        // A search that works again is a cleared cause: the same failure later is worth saying again.
        _lastSearchFailureFlash = null;

        return [.. ParseResults(output)];
    }

    /// <summary>Plain words for what a host can do, never the raw exception text — yt-dlp's own
    /// stderr line is for the log, not the console.</summary>
    private static string DescribeSearchFailure(Exception ex) => ex switch
    {
        YtDlpUnavailableException =>
            "YouTube: search failed — yt-dlp is not installed and could not be downloaded. Check this computer is online.",
        YtDlpTimeoutException =>
            "YouTube: search failed — it took too long. Check your internet connection and try again.",
        KHostException { ReferenceCode: YtDlpFailure.OutdatedCode } =>
            "YouTube: search failed — yt-dlp on this machine looks too old. Run 'yt-dlp -U' to update it.",
        FileNotFoundException =>
            "YouTube: search failed — yt-dlp was not found at its configured path. Check the yt-dlp Path setting.",
        _ =>
            "YouTube: search failed — yt-dlp could not be reached. Check your internet connection, or the yt-dlp Path setting.",
    };

    private void FlashSearchFailureOnce(string message)
    {
        if (message == _lastSearchFailureFlash) return;

        _lastSearchFailureFlash = message;
        _flash.Show(message, FlashType.Warning);
    }

    /// <summary>One JSON object per line. A blank or half-written line is skipped, not thrown over.</summary>
    private List<MediaSearchEntity> ParseResults(string output)
    {
        var rows = new List<(string VideoId, string RawTitle, string ChannelName, TimeSpan? Duration, bool Verified, string Thumbnail)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                // Without an id there is nothing to enqueue or open later, so the row is no use.
                if (!root.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } videoId)
                    continue;

                rows.Add((
                    videoId,
                    root.TryGetProperty("title", out var title) ? title.GetString() ?? videoId : videoId,
                    root.TryGetProperty("channel", out var channel) ? channel.GetString() ?? "" : "",
                    ReadDuration(root),
                    root.TryGetProperty("channel_is_verified", out var verified) && verified.ValueKind is JsonValueKind.True,
                    YouTubeThumbnails.Pick(root)));
            }
            catch (JsonException)
            {
                continue;
            }
        }

        // Parsed as a set, not row by row: one result that names the artist outright settles the
        // orientation of the ones that only have a dash to go on.
        var parsed = YouTubeTitleParser.ParseAll(
            [.. rows.Select(row => (row.RawTitle, row.ChannelName))]);

        return
        [
            .. rows.Select((row, index) => new MediaSearchEntity
            {
                // The video's own title, not the parse: a host picking between near-identical
                // karaoke uploads needs the words YouTube shows, decoration included.
                Title = row.RawTitle,
                Artist = parsed[index].Artist,
                SourceDisplayName = DisplayName,
                Source = SourceName,
                ForeignKey = row.VideoId,
                Duration = row.Duration,
                Notes = BuildNotes(row.ChannelName, row.RawTitle, parsed[index].Title, row.VideoId),
                Fields = new Dictionary<string, string>
                {
                    [ThumbnailKey] = row.Thumbnail,
                    [PublisherKey] = row.Verified ? row.ChannelName + VerifiedMark : row.ChannelName,
                    // Carried rather than re-parsed at import: ParseAll settles a dash-only title
                    // against the whole result set, and one row on its own can orient the wrong way.
                    [CleanTitleKey] = parsed[index].Title,
                },
                SupportedActions = Actions,
            })
        ];
    }

    /// <summary>Channel name, the raw title whenever the parse changed it, and the watch URL.
    /// The library row keeps the parsed title, so this is the only record of what it came from.</summary>
    private static string BuildNotes(string channelName, string rawTitle, string parsedTitle, string videoId)
    {
        var parts = new List<string>(3);

        if (channelName.Length > 0)
            parts.Add(channelName);

        if (parsedTitle != rawTitle)
            parts.Add($"“{rawTitle}”");

        parts.Add(WatchUrl(videoId));

        return string.Join(" — ", parts);
    }

    private static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>Seconds, null for anything without a real one: a live stream reports none.</summary>
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
        Process.Start(new ProcessStartInfo(WatchUrl(entity.ForeignKey)) { UseShellExecute = true });

        return Task.CompletedTask;
    }

    // The queue owns who is selected and performances own the enqueue; neither may take the
    // other, so a caller that wants both composes them.
    private async Task EnqueueForSelectedSingerAsync(Guid mediaId)
    {
        if (_queue.SelectedUserId is not { } singerId)
        {
            _logger.LogWarning("Cannot enqueue: no singer selected");
            return;
        }

        await _performances.CreateAndEnqueueAsync(new()
        {
            MediaId = mediaId,
            SingerId = singerId,
            CreatedDate = DateTime.UtcNow,
        });
    }

    private async Task DownloadAndEnqueueAsync(MediaSearchEntity entity)
    {
        // Read per call: the host can hot-reload MediaDirectory, so a cached value could go stale.
        var directory = Path.Combine(_media.MediaDirectory, "youtube");
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, $"{entity.ForeignKey}.mp4");
        var request = new MediaImportRequest
        {
            FilePath = destination,
            // The list shows the raw video title; the library keeps the parse the search already did.
            Title = entity.Fields.TryGetValue(CleanTitleKey, out var cleanTitle) && cleanTitle.Length > 0
                ? cleanTitle
                : entity.Title,
            Artist = entity.Artist,
            Duration = entity.Duration,
            Notes = entity.Notes,
            Source = DisplayName,
        };

        // A re-click must not re-fetch a video already sitting in the library's cache.
        if (File.Exists(destination))
        {
            var readyId = await _media.ImportAsync(request);
            await EnqueueForSelectedSingerAsync(readyId);
            return;
        }

        if (!_downloadsInFlight.TryAdd(entity.ForeignKey, 0))
            return;

        try
        {
            // Enqueue immediately, before the download runs, so the singer's queue shows the
            // Downloading spinner row the moment the host clicks, not minutes later on slow venue internet.
            var ticket = await _media.BeginImportAsync(request);
            await EnqueueForSelectedSingerAsync(ticket.MediaId);

            var destinationsSeen = 0;
            void OnLine(string line)
            {
                double? fraction;
                (destinationsSeen, fraction) = YtDlpProgressParser.Parse(destinationsSeen, line);

                if (fraction is { } value)
                    _ = ReportProgressSafelyAsync(ticket.MediaId, value);
            }

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
                        // Without it yt-dlp rewrites its progress line in place with carriage
                        // returns, so line-by-line streaming never sees an update.
                        "--newline",
                    ],
                    ticket.Cancellation,
                    OnLine);
            }
            catch (OperationCanceledException)
            {
                await CleanUpAfterCancelAsync(directory, entity.ForeignKey, destination, ticket.MediaId);
                throw;
            }
            catch (Exception ex)
            {
                // Not rethrown: the host would only log it generically, and this is where the
                // YouTube wording lives. A cancel took the branch above and still propagates.
                _logger.LogWarning(ex, "YouTube download of '{VideoId}' failed", entity.ForeignKey);

                var reason = YtDlpFailure.Describe(ex);
                var detail = YtDlpFailure.Detail(ex);

                // The detail is for the Downloads page a host opens on purpose; the flash stays plain.
                await _media.FailImportAsync(ticket.MediaId, detail is null ? reason : $"{reason} ({detail})");
                _flash.Show($"YouTube: could not download '{request.Title}' — {reason}", FlashType.Warning);
                return;
            }

            if (!File.Exists(destination))
            {
                _logger.LogWarning(
                    "yt-dlp did not produce '{Destination}' for '{VideoId}': {Output}", destination, entity.ForeignKey, output);

                const string reason = "yt-dlp finished without producing a file.";

                await _media.FailImportAsync(ticket.MediaId, reason);
                _flash.Show($"YouTube: could not download '{request.Title}' — {reason}", FlashType.Warning);
                return;
            }

            await _media.CompleteImportAsync(ticket.MediaId);
        }
        finally
        {
            _downloadsInFlight.TryRemove(entity.ForeignKey, out _);
        }
    }

    /// <summary>Fire-and-forget from a synchronous callback: caught here rather than left to
    /// surface unobserved, since a failed progress update must never take the download down too.</summary>
    private async Task ReportProgressSafelyAsync(Guid mediaId, double fraction)
    {
        try
        {
            await _media.ReportDownloadProgressAsync(mediaId, fraction);
        }
        catch
        {
            // Best-effort UI update only; nothing here is worth failing the download over.
        }
    }

    /// <summary>A cancelled download leaves .part/.ytdl and (bv+ba) per-stream fragments, all
    /// "{foreignKey}.*"; Path.Exists (not File.Exists) routes a leftover dir to FailImportAsync.</summary>
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
            await _media.FailImportAsync(mediaId, "cancelled, and a partial download could not be removed");
        else
            await _media.DiscardImportAsync(mediaId);
    }
}
