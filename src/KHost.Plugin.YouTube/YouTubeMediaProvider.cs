using KHost.Plugins.Sdk.Models;
using KHost.Plugins.Sdk.Services;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml;

namespace KHost.Plugin.YouTube;

public class YouTubeMediaProvider : IMediaProvider, IDisposable
{
    private const int MaxAllowedResults = 50;

    private readonly YouTubeSettings _settings;
    private readonly HttpClient _http;

    public YouTubeMediaProvider(IPlugin plugin)
        : this(plugin, new HttpClientHandler())
    {
    }

    public YouTubeMediaProvider(IPlugin plugin, HttpMessageHandler handler)
    {
        _settings = plugin.BindSettings<YouTubeSettings>();
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/youtube/v3/") };

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
        var apiKey = _settings.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("YouTube Data API key is not configured; set it on the Plugins settings page.");

        // The API pages by continuation token, not offset — only the first page is served.
        if (pageNumber > 1)
            return [];

        var maxResults = Math.Clamp(pageSize > 0 ? pageSize : _settings.MaxResults, 1, MaxAllowedResults);
        var searchUrl = $"search?part=snippet&type=video&maxResults={maxResults}&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}";

        using var searchDocument = JsonDocument.Parse(await _http.GetStringAsync(searchUrl));

        var videos = searchDocument.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (
                Id: item.GetProperty("id").GetProperty("videoId").GetString()!,
                Snippet: item.GetProperty("snippet")))
            .Select(video => (
                video.Id,
                // Titles come HTML-encoded (&amp;, &#39;) from the API.
                Title: WebUtility.HtmlDecode(video.Snippet.GetProperty("title").GetString() ?? ""),
                Channel: WebUtility.HtmlDecode(video.Snippet.GetProperty("channelTitle").GetString() ?? "")))
            .ToList();

        var durations = await ReadDurationsAsync(videos.Select(v => v.Id), apiKey);

        return [.. videos.Select(video => new MediaSearchEntity
        {
            DisplayName = video.Title,
            SourceDisplayName = DisplayName,
            Source = SourceName,
            ForeignKey = video.Id,
            Duration = durations.GetValueOrDefault(video.Id),
            Notes = video.Channel,
            SupportedActions = Actions,
        })];
    }

    public void Dispose() => _http.Dispose();

    private async Task<Dictionary<string, TimeSpan>> ReadDurationsAsync(IEnumerable<string> videoIds, string apiKey)
    {
        var ids = string.Join(',', videoIds);

        if (ids.Length == 0)
            return [];

        using var document = JsonDocument.Parse(
            await _http.GetStringAsync($"videos?part=contentDetails&id={ids}&key={Uri.EscapeDataString(apiKey)}"));

        var durations = new Dictionary<string, TimeSpan>();

        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var iso = item.GetProperty("contentDetails").GetProperty("duration").GetString();

            if (iso is null) continue;

            try
            {
                durations[id] = XmlConvert.ToTimeSpan(iso);
            }
            catch (FormatException)
            {
                // Live streams report P0D and oddities; a missing duration is fine.
            }
        }

        return durations;
    }

    private Task OpenInBrowserAsync(MediaSearchEntity entity)
    {
        // KHost runs on the host's own machine, so the default browser is the right target.
        Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={entity.ForeignKey}") { UseShellExecute = true });

        return Task.CompletedTask;
    }
}
