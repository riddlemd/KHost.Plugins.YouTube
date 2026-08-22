using KHost.Plugin.YouTube;
using KHost.Plugins.Sdk.Services;
using System.Net;

namespace KHost.Plugin.YouTube.Tests;

public class YouTubeMediaProviderTests
{
    private const string SearchJson = """
        {
          "items": [
            {
              "id": { "videoId": "abc123" },
              "snippet": { "title": "Africa &#39;Karaoke&#39; &amp; Lyrics", "channelTitle": "Karaoke Channel" }
            },
            {
              "id": { "videoId": "def456" },
              "snippet": { "title": "Wonderwall Karaoke", "channelTitle": "Sing King" }
            }
          ]
        }
        """;

    private const string VideosJson = """
        {
          "items": [
            { "id": "abc123", "contentDetails": { "duration": "PT4M35S" } },
            { "id": "def456", "contentDetails": { "duration": "P0D" } }
          ]
        }
        """;

    private readonly IPlugin _plugin = Substitute.For<IPlugin>();
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly YouTubeMediaProvider _provider;

    public YouTubeMediaProviderTests()
    {
        _plugin.BindSettings<YouTubeSettings>().Returns(new YouTubeSettings { ApiKey = "test-key", MaxResults = 10 });

        _handler.Responses["search"] = SearchJson;
        _handler.Responses["videos"] = VideosJson;

        _provider = new YouTubeMediaProvider(_plugin, _handler);
    }

    [Fact]
    public async Task SearchAsync_NoApiKey_Throws()
    {
        _plugin.BindSettings<YouTubeSettings>().Returns(new YouTubeSettings());
        var provider = new YouTubeMediaProvider(_plugin, _handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync("africa"));
    }

    [Fact]
    public async Task SearchAsync_MapsResultsWithDecodedTitles()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal(2, results.Count);
        Assert.Equal("Africa 'Karaoke' & Lyrics", results[0].Title);
        Assert.Equal("abc123", results[0].ForeignKey);
        Assert.Equal("YouTube", results[0].Source);
        Assert.Equal("Karaoke Channel", results[0].Notes);
    }

    [Fact]
    public async Task SearchAsync_LeavesArtistEmpty_AndKeepsTheChannelAsANote()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // The channel uploaded the video, it did not perform the song, so it must not reach the
        // artist column the console now renders beside the title.
        Assert.Equal(string.Empty, results[0].Artist);
        Assert.Equal("Karaoke Channel", results[0].Notes);
    }

    [Fact]
    public async Task SearchAsync_ParsesIsoDurations()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal(TimeSpan.FromSeconds(4 * 60 + 35), results[0].Duration);
        // P0D (live stream) parses to zero rather than being dropped; either way it must not throw.
        Assert.True(results[1].Duration is null || results[1].Duration == TimeSpan.Zero);
    }

    [Fact]
    public async Task SearchAsync_EscapesQueryAndUsesConfiguredMax()
    {
        await _provider.SearchAsync("rick & morty");

        var searchUrl = _handler.Requests.Single(u => u.Contains("search?"));

        Assert.Contains("q=rick%20%26%20morty", searchUrl);
        Assert.Contains("maxResults=10", searchUrl);
        Assert.Contains("key=test-key", searchUrl);
    }

    [Fact]
    public async Task SearchAsync_PageSizeOverridesConfiguredMax()
    {
        await _provider.SearchAsync("africa", pageSize: 5);

        Assert.Contains("maxResults=5", _handler.Requests.Single(u => u.Contains("search?")));
    }

    [Fact]
    public async Task SearchAsync_SecondPage_ReturnsEmptyWithoutCallingApi()
    {
        var results = await _provider.SearchAsync("africa", pageNumber: 2);

        Assert.Empty(results);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task SearchAsync_EmptySearchResponse_ReturnsEmptyWithoutDurationCall()
    {
        _handler.Responses["search"] = """{ "items": [] }""";

        var results = await _provider.SearchAsync("zzzzz");

        Assert.Empty(results);
        Assert.DoesNotContain(_handler.Requests, u => u.Contains("videos?"));
    }

    [Fact]
    public void Actions_ExposesOpenOnYouTube()
    {
        var action = Assert.Single(_provider.Actions);

        Assert.Equal("Open", action.DisplayName);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // AbsoluteUri keeps percent-escaping; ToString() would decode %20 back to a space.
            var url = request.RequestUri!.AbsoluteUri;

            Requests.Add(url);

            var body = Responses.FirstOrDefault(r => url.Contains(r.Key)).Value
                ?? throw new InvalidOperationException($"No canned response for {url}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
