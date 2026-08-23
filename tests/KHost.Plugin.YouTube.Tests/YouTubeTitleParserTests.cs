using KHost.Plugin.YouTube;

namespace KHost.Plugin.YouTube.Tests;

public class YouTubeTitleParserTests
{
    [Theory]
    [InlineData("Toto - Africa (Karaoke Version)", "Africa", "Toto")]
    [InlineData("Africa (In the Style of Toto) [Karaoke Version]", "Africa", "Toto")]
    [InlineData("Wonderwall Karaoke", "Wonderwall", "")]
    [InlineData("Africa (Karaoke) & Lyrics", "Africa", "")]
    [InlineData("Oasis - Wonderwall (Karaoke Version) | Sing King Karaoke", "Wonderwall", "Oasis")]
    [InlineData("\"Bohemian Rhapsody\" by Queen (Karaoke with Lyrics)", "Bohemian Rhapsody", "Queen")]
    [InlineData("Somebody to Love (Originally Performed by Queen) [Instrumental]", "Somebody to Love", "Queen")]
    [InlineData("Blink-182 - All The Small Things (Karaoke)", "All The Small Things", "Blink-182")]
    [InlineData("Toto - Africa (Karaoke Version) [HD]", "Africa", "Toto")]
    [InlineData("Africa (Made Popular by Toto) [4K]", "Africa", "Toto")]
    [InlineData("Africa (As Made Famous by Toto) [Sing Along]", "Africa", "Toto")]
    [InlineData("Wonderwall | Sing King Karaoke", "Wonderwall", "")]
    public void Parse_SplitsTitleAndArtist(string rawTitle, string expectedTitle, string expectedArtist)
    {
        var (title, artist) = YouTubeTitleParser.Parse(rawTitle);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
    }

    [Theory]
    [InlineData("Africa - Toto | Karaoke Version | KaraFun", "KaraFun Karaoke", "Africa", "Toto")]
    [InlineData("Toto - Africa - Karaoke Instrumental Lyrics - ObsKure", "ObsKure Karaoke", "Africa", "Toto")]
    [InlineData("Toto | Africa | Karaoke", "Reekies Karaoke", "Africa", "Toto")]
    [InlineData("Toto - Africa (Karaoke Version) with Lyrics On Screen", "Zoom Karaoke Official", "Africa", "Toto")]
    public void Parse_ChannelAwareRealWorldTitles_SplitCorrectly(string rawTitle, string channelName, string expectedTitle, string expectedArtist)
    {
        var (title, artist) = YouTubeTitleParser.Parse(rawTitle, channelName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
    }

    [Fact]
    public void Parse_ChannelOverlapSegment_IsNotDroppedWhenItIsTheOnlySegmentLeft()
    {
        // "KaraFun" overlaps the channel and would normally be dropped, but it is the only pipe
        // segment left once "Karaoke Version" is stripped as plain junk — dropping it too would
        // erase the title entirely, so the guard has to keep it.
        var (title, artist) = YouTubeTitleParser.Parse("KaraFun | Karaoke Version", "KaraFun Karaoke");

        Assert.Equal("KaraFun", title);
        Assert.Equal("", artist);
    }

    [Fact]
    public void Parse_TwoPipeSegments_SplitArtistFirst()
    {
        var (title, artist) = YouTubeTitleParser.Parse("Adele | Hello");

        Assert.Equal("Hello", title);
        Assert.Equal("Adele", artist);
    }

    [Fact]
    public void Parse_HyphenatedArtist_DoesNotSplitOnTheInnerHyphen()
    {
        // "Blink-182" has no spaces around its hyphen, so only the " - " before the song title
        // is a real separator.
        var (title, artist) = YouTubeTitleParser.Parse("Blink-182 - All The Small Things (Karaoke)");

        Assert.Equal("Blink-182", artist);
        Assert.DoesNotContain("182", title);
    }

    [Fact]
    public void Parse_NoSeparatorOrJunk_PassesThePlainTitleThrough()
    {
        var (title, artist) = YouTubeTitleParser.Parse("Sweet Caroline");

        Assert.Equal("Sweet Caroline", title);
        Assert.Equal(string.Empty, artist);
    }

    [Fact]
    public void Parse_TitleIsAllJunk_FallsBackToTheRawString()
    {
        // Stripping every junk phrase leaves nothing to show as a title, so the raw string is a
        // better result than an empty one.
        var (title, artist) = YouTubeTitleParser.Parse("Karaoke Version");

        Assert.Equal("Karaoke Version", title);
        Assert.Equal(string.Empty, artist);
    }

    [Fact]
    public void Parse_JunkBracketInTheMiddle_IsStrippedNotJustTrailingOnes()
    {
        var (title, artist) = YouTubeTitleParser.Parse("Toto - Africa (Karaoke Version) [HD]");

        Assert.Equal("Africa", title);
        Assert.Equal("Toto", artist);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsItUnchanged()
    {
        var (title, artist) = YouTubeTitleParser.Parse("");

        Assert.Equal("", title);
        Assert.Equal(string.Empty, artist);
    }
}
