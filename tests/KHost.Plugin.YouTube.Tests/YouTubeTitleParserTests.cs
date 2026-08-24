using KHost.Plugin.YouTube;

namespace KHost.Plugin.YouTube.Tests;

public class YouTubeTitleParserTests
{
    [Theory]
    [InlineData("Toto - Africa (Karaoke Version)", "Africa", "Toto")]
    [InlineData("Africa (In the Style of Toto) [Karaoke Version]", "Africa", "Toto")]
    [InlineData("Wonderwall Karaoke", "Wonderwall", "")]
    [InlineData("Africa (Karaoke) & Lyrics", "Africa", "")]
    [InlineData("\"Bohemian Rhapsody\" by Queen (Karaoke with Lyrics)", "Bohemian Rhapsody", "Queen")]
    [InlineData("Somebody to Love (Originally Performed by Queen) [Instrumental]", "Somebody to Love", "Queen")]
    [InlineData("Blink-182 - All The Small Things (Karaoke)", "All The Small Things", "Blink-182")]
    [InlineData("Toto - Africa (Karaoke Version) [HD]", "Africa", "Toto")]
    [InlineData("Africa (Made Popular by Toto) [4K]", "Africa", "Toto")]
    [InlineData("Africa (As Made Famous by Toto) [Sing Along]", "Africa", "Toto")]
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
    // Without a channel, "Sing King Karaoke" as a trailing pipe segment is indistinguishable from real
    // artist/title content ("The Steve Miller Band Karaoke Version" has the exact same shape) — only
    // the channel-overlap check can tell it apart, so these need the real channel name to pass.
    [InlineData("Oasis - Wonderwall (Karaoke Version) | Sing King Karaoke", "Sing King", "Wonderwall", "Oasis")]
    [InlineData("Wonderwall | Sing King Karaoke", "Sing King", "Wonderwall", "")]
    public void Parse_ChannelAwareRealWorldTitles_SplitCorrectly(string rawTitle, string channelName, string expectedTitle, string expectedArtist)
    {
        var (title, artist) = YouTubeTitleParser.Parse(rawTitle, channelName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedArtist, artist);
    }

    // Every case below is a real title from spikes/title-parse-corpus/corpus.jsonl, cited with its
    // channel — one per corpus-spike rule (see report.txt "Part 4: recommended grammar rules").
    [Theory]
    // Rule 1 — TitleFirstChannels expansion: EasyKaraoke is corpus-derived (90/90 dash rows Title -
    // Artist), unlike the default Artist - Title assumption.
    [InlineData("Rocket Man - Elton John (Karaoke Version)", "EasyKaraoke", "Rocket Man", "Elton John")]
    // Rule 1 (negative/collision guard) — "Piano Karaoke" is deliberately NOT in TitleFirstChannels
    // (see the field's comment): this channel stays Artist-first, so "(Piano Karaoke)" is real
    // leftover bracket content, not a rule failure.
    [InlineData("Fleetwood Mac - Dreams (Piano Karaoke)", "Sing2Piano | Piano Karaoke Instrumentals", "Dreams (Piano Karaoke)", "Fleetwood Mac")]
    // Rule 2 — "·" as a separator, same as spaced dash.
    [InlineData("reo speedwagon · can't fight this feeling (karaoke version)", "fab4 karaoke", "can't fight this feeling", "reo speedwagon")]
    // Rule 2 — "•" as a separator; also exercises the OverlapsChannel leftover-length guard (the
    // channel-overlap check that stops a "•"-delimited bracket from swallowing the whole title -
    // see OverlapsChannel's comment). "[UVR]" isn't recognized junk vocabulary, so it's expected to
    // survive.
    [InlineData("Led Zeppelin  • Stairway To Heaven (CC Karaoke / Instrumental) [UVR]", "CC Karaoke", "Stairway To Heaven [UVR]", "Led Zeppelin")]
    // OverlapsChannel — a trailing segment naming the channel is dropped whole, decoration and all,
    // even when that decoration is a lot longer than the channel name itself. (A length-based cutoff
    // was tried here during Rule 2's development to stop a channel-name-bearing bracket from
    // swallowing the title, but Rule 3's junk-vocabulary bracket stripping already removes that
    // bracket earlier in the pipeline, so the cutoff only cost rows like this one.)
    [InlineData("Panic! At The Disco - I Write Sins Not Tragedies - NOX Karaoke (with background vocals)", "Nox Karaoke", "I Write Sins Not Tragedies", "Panic! At The Disco")]
    // Rule 3 — "cc" added to junk vocabulary, and 2+ junk words chained without "with" ("Karaoke
    // Instrumental").
    [InlineData("Journey - Don't Stop Believing [CC] [Karaoke Instrumental]", "CC Karaoke X", "Don't Stop Believing", "Journey")]
    // Rule 4 — unbracketed `in the style of "Artist"` (Stingray Karaoke's house style).
    [InlineData("Folsom Prison Blues in the style of \"Johnny Cash\" with lyrics (no lead vocal)", "Stingray Karaoke", "Folsom Prison Blues", "Johnny Cash")]
    // Rule 5 — pipe-segment junk filter no longer wipes a segment merely for containing "karaoke";
    // "Title Karaoke | Artist Karaoke Version" is Vocal Star Karaoke's own pipe convention
    // (PipeTitleFirstChannels), the reverse of YouTube's default "Artist | Title".
    [InlineData("The Joker Karaoke | The Steve Miller Band Karaoke Version", "Vocal Star Karaoke", "The Joker", "The Steve Miller Band")]
    [InlineData("American Woman Karaoke | The Guess Who Karaoke Version", "Vocal Star Karaoke", "American Woman", "The Guess Who")]
    // Rule 5 (regression guard) — the channel's own branding hyphenates ("Vocal-Star") where the
    // channel name has a space; IsPipeSegmentJunkOrChannelPromo folds hyphen/space together so this
    // pure-promo segment is still recognized and dropped instead of misread as the artist.
    [InlineData("Black Eyed Peas - Boom Boom Pow | With Lyrics HD Vocal-Star Karaoke 4K", "Vocal Star Karaoke", "Boom Boom Pow", "Black Eyed Peas")]
    // Rule 6 — a bracket mixing a junk phrase with a genuine qualifier via "with" is dropped whole.
    [InlineData("Extreme - More Than Words (Karaoke Version with Harmony) with Lyrics On Screen", "Zoom Karaoke Official", "More Than Words", "Extreme")]
    [InlineData("Simon And Garfunkel - The Sound Of Silence (Karaoke Version with Harmony) with Lyrics On Screen", "Zoom Karaoke Official", "The Sound Of Silence", "Simon And Garfunkel")]
    public void Parse_CorpusSpikeRules_SplitCorrectly(string rawTitle, string channelName, string expectedTitle, string expectedArtist)
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
