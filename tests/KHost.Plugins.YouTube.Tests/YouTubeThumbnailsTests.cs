using System.Text.Json;
using KHost.Plugins.YouTube;

namespace KHost.Plugins.YouTube.Tests;

public class YouTubeThumbnailsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Pick_TheSizesYouTubeActuallyReturns_TakesTheSmallerThatStillCoversTheCell()
    {
        // Both are the same picture at different sizes; the console draws it at 88px.
        var root = Parse("""
        {"thumbnails":[
          {"url":"https://i.ytimg.com/vi/x/hq720.jpg?small","width":360,"height":202},
          {"url":"https://i.ytimg.com/vi/x/hq720.jpg?big","width":720,"height":404}]}
        """);

        Assert.Equal("https://i.ytimg.com/vi/x/hq720.jpg?small", YouTubeThumbnails.Pick(root));
    }

    [Fact]
    public void Pick_EverythingIsTooSmall_TakesTheLargestThereIs()
    {
        var root = Parse("""
        {"thumbnails":[
          {"url":"tiny","width":48,"height":27},
          {"url":"less-tiny","width":120,"height":68}]}
        """);

        Assert.Equal("less-tiny", YouTubeThumbnails.Pick(root));
    }

    [Fact]
    public void Pick_AnExactFit_TakesIt()
    {
        var root = Parse("""{"thumbnails":[{"url":"exact","width":360,"height":202}]}""");

        Assert.Equal("exact", YouTubeThumbnails.Pick(root));
    }

    [Fact]
    public void Pick_NoThumbnailsAtAll_IsEmpty()
        => Assert.Equal(string.Empty, YouTubeThumbnails.Pick(Parse("""{"id":"x"}""")));

    [Fact]
    public void Pick_AnEmptyList_IsEmpty()
        => Assert.Equal(string.Empty, YouTubeThumbnails.Pick(Parse("""{"thumbnails":[]}""")));

    [Fact]
    public void Pick_AnEntryWithNoUrl_IsSkipped()
    {
        var root = Parse("""{"thumbnails":[{"width":360},{"url":"real","width":360}]}""");

        Assert.Equal("real", YouTubeThumbnails.Pick(root));
    }

    /// <summary>An unsized entry is better than no picture, but loses to any sized one.</summary>
    [Fact]
    public void Pick_AnUnsizedEntryBesideASizedOne_PrefersTheSized()
    {
        var root = Parse("""{"thumbnails":[{"url":"unsized"},{"url":"sized","width":360}]}""");

        Assert.Equal("sized", YouTubeThumbnails.Pick(root));
    }

    [Fact]
    public void Pick_OnlyUnsizedEntries_StillReturnsOne()
        => Assert.Equal("unsized", YouTubeThumbnails.Pick(Parse("""{"thumbnails":[{"url":"unsized"}]}""")));
}
