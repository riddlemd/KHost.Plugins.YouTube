using KHost.Plugin.YouTube;
using KHost.Plugins.Sdk.Services;

namespace KHost.Plugin.YouTube.Tests;

public class YouTubeMediaProviderTests
{
    // What `yt-dlp --dump-json --flat-playlist` emits: one object per line, not an array.
    private const string SearchOutput = """
        {"id":"abc123","title":"Africa (Karaoke) & Lyrics","channel":"Karaoke Channel","duration":275}
        {"id":"def456","title":"Wonderwall Karaoke","channel":"Sing King","duration":null}
        """;

    private readonly IPlugin _plugin = Substitute.For<IPlugin>();
    private readonly FakeRunner _runner = new() { Output = SearchOutput };
    private readonly YouTubeMediaProvider _provider;

    public YouTubeMediaProviderTests()
    {
        _plugin.BindSettings<YouTubeSettings>().Returns(new YouTubeSettings { MaxResults = 10 });

        _provider = new YouTubeMediaProvider(_plugin, _runner.RunAsync);
    }

    [Fact]
    public async Task SearchAsync_MapsResults()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal(2, results.Count);
        Assert.Equal("Africa (Karaoke) & Lyrics", results[0].Title);
        Assert.Equal("abc123", results[0].ForeignKey);
        Assert.Equal("YouTube", results[0].Source);
        Assert.Equal("Karaoke Channel", results[0].Notes);
        Assert.Equal(TimeSpan.FromSeconds(275), results[0].Duration);
    }

    [Fact]
    public async Task SearchAsync_LeavesArtistEmpty_AndKeepsTheChannelAsANote()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // The channel uploaded the video, it did not perform the song, so it must not reach the
        // artist column the console renders beside the title.
        Assert.Equal(string.Empty, results[0].Artist);
        Assert.Equal("Karaoke Channel", results[0].Notes);
    }

    [Fact]
    public async Task SearchAsync_NullDuration_LeavesItNull()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Null(results[1].Duration);
    }

    [Fact]
    public async Task SearchAsync_ZeroDuration_LeavesItNull()
    {
        // A live stream reports 0 rather than omitting the field, and zero renders as "0:00" —
        // a definite-looking length for a song whose length is not known.
        _runner.Output = """{"id":"live1","title":"Karaoke live stream","channel":"c","duration":0}""";

        var results = await _provider.SearchAsync("karaoke live");

        Assert.Null(Assert.Single(results).Duration);
    }

    [Fact]
    public async Task SearchAsync_AsksForTheConfiguredNumberOfResults()
    {
        await _provider.SearchAsync("rick & morty");

        var arguments = _runner.Calls.Single();

        Assert.Equal("ytsearch10:rick & morty", arguments[0]);
        Assert.Contains("--dump-json", arguments);

        // Without it yt-dlp resolves every hit in turn, which is a page load per row.
        Assert.Contains("--flat-playlist", arguments);
    }

    [Fact]
    public async Task SearchAsync_PassesTheQueryAsOneArgument_SoQuotesCannotSplitIt()
    {
        await _provider.SearchAsync("don't stop \"believin\"");

        // One argument, verbatim: joining these into a command line is how a title with a quote
        // turns into extra arguments.
        Assert.Equal("ytsearch10:don't stop \"believin\"", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_PageSizeOverridesConfiguredMax()
    {
        await _provider.SearchAsync("africa", pageSize: 5);

        Assert.Equal("ytsearch5:africa", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_ClampsAnAbsurdPageSize()
    {
        await _provider.SearchAsync("africa", pageSize: 5000);

        Assert.Equal("ytsearch50:africa", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_SecondPage_ReturnsEmptyWithoutRunningAnything()
    {
        // ytsearch takes a count, not an offset, so page two would re-run page one's search.
        var results = await _provider.SearchAsync("africa", pageNumber: 2);

        Assert.Empty(results);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsEmptyWithoutRunningAnything()
    {
        var results = await _provider.SearchAsync("   ");

        Assert.Empty(results);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmpty()
    {
        _runner.Output = "";

        Assert.Empty(await _provider.SearchAsync("zzzzz"));
    }

    [Fact]
    public async Task SearchAsync_SkipsARowItCannotUse()
    {
        // A truncated line and a row with no id: neither can be enqueued or opened, and neither is
        // worth failing the whole search over.
        _runner.Output = """
            {"id":"abc123","title":"Fine","channel":"c","duration":10}
            {"title":"No id here","duration":10}
            {"id":"trunc","title":"Half a li
            """;

        var results = await _provider.SearchAsync("africa");

        Assert.Equal("abc123", Assert.Single(results).ForeignKey);
    }

    [Fact]
    public void Actions_ExposesOpenOnYouTube()
    {
        var action = Assert.Single(_provider.Actions);

        Assert.Equal("Open", action.DisplayName);
    }

    private sealed class FakeRunner
    {
        public string Output { get; set; } = "";
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);

            return Task.FromResult(Output);
        }
    }
}
