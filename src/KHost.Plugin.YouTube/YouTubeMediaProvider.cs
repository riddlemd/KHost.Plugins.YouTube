using KHost.Plugins.Sdk.Models;
using KHost.Plugins.Sdk.Services;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KHost.Plugin.YouTube;

public class YouTubeMediaProvider : IMediaProvider
{
    private const int MaxAllowedResults = 50;

    private readonly YouTubeSettings _settings;
    private readonly YtDlpRunner _run;

    public YouTubeMediaProvider(IPlugin plugin)
        : this(plugin, BuildRunner(plugin))
    {
    }

    public YouTubeMediaProvider(IPlugin plugin, YtDlpRunner run)
    {
        _settings = plugin.BindSettings<YouTubeSettings>();
        _run = run;

        Actions = [
            new() {
                DisplayName = "Open",
                Description = "Open on YouTube",
                Icon = "youtube",
                PerformAsync = OpenInBrowserAsync,
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
                $"ytsearch{count.ToString(CultureInfo.InvariantCulture)}:{query}",
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

                entity = new MediaSearchEntity
                {
                    // Artist stays empty on purpose: a video title is one string no parse splits
                    // reliably, and the channel is the uploader — "Sing King" did not perform it.
                    Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? videoId : videoId,
                    SourceDisplayName = DisplayName,
                    Source = SourceName,
                    ForeignKey = videoId,
                    Duration = ReadDuration(root),
                    Notes = root.TryGetProperty("channel", out var channel) ? channel.GetString() ?? "" : "",
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

    private static YtDlpRunner BuildRunner(IPlugin plugin)
    {
        var settings = plugin.BindSettings<YouTubeSettings>();

        var resolver = new YtDlpResolver(
            settings.YtDlpPath,
            Path.Combine(AppContext.BaseDirectory, "cache", "tools"));

        return new YtDlp(resolver, settings.AutoUpdate).RunAsync;
    }

    private Task OpenInBrowserAsync(MediaSearchEntity entity)
    {
        // KHost runs on the host's own machine, so the default browser is the right target.
        Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={entity.ForeignKey}") { UseShellExecute = true });

        return Task.CompletedTask;
    }
}
