using System.Text.RegularExpressions;

namespace KHost.Plugin.YouTube;

/// <summary>
/// Splits a YouTube video title into a song title and, where the title carries one, an artist.
/// Karaoke channels bury the artist inside decoration ("(In the Style of X)") as often as they use
/// YouTube's own "Artist - Title" convention, so both shapes have to be tried.
/// </summary>
public static class YouTubeTitleParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Junk vocabulary shared between the bracket-stripper and the bare-trailing-word stripper.
    private const string JunkVocabulary =
        @"karaoke(?:\s+version)?|instrumental|lyric\s+video|lyrics?|with\s+lyrics|lyrics\s+on\s+screen|"
        + @"backing\s+track|official\s+(?:video|audio|music\s+video)|hd|4k|sing\s+along|minus\s+one|"
        + @"no\s+lead\s+vocal|guide\s+vocal";

    // KaraFun's house style is "Title - Artist", the reverse of every channel this parser has
    // otherwise seen — an observed convention, not a guess, so the set stays narrow.
    private static readonly string[] TitleFirstChannels = ["karafun"];

    // First match wins, so the more specific "X by Y" pattern is tried only when nothing that names
    // the segment explicitly ("in the style of", etc.) has already claimed the artist. TitleGroup is
    // null for the bracket carriers — there is no title text inside them, just decoration to drop —
    // and set for the quoted-title carrier, whose match spans the title text too and must keep it.
    private static readonly (Regex Pattern, int ArtistGroup, int? TitleGroup)[] ArtistCarriers =
    [
        (new Regex(@"[\(\[]\s*in\s+the\s+style\s+of\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*originally\s+performed\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*made\s+popular\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*as\s+made\s+famous\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex("""["“]([^"”]+)["”]\s+by\s+([^\(\)\[\]|]+)""", Options), 2, 1),
    ];

    // A bracket can carry more than one junk phrase ("Karaoke with Lyrics"), not just one alternative.
    private static readonly Regex BracketedJunk = new(
        $@"[\(\[]\s*(?:{JunkVocabulary})(?:\s+with\s+(?:{JunkVocabulary}))*\s*[\)\]]", Options);

    // Peels one trailing junk phrase at a time (looped in StripTrailingJunkChain), so a run like
    // "- Karaoke Instrumental Lyrics" comes off word by word. The connector is optional so a bare
    // junk phrase with nothing before it (the whole title is junk) still matches.
    private static readonly Regex TrailingJunkLink = new(
        $@"(?:\s*[-–—&|])?\s*(?:with\s+)?(?:{JunkVocabulary})\s*$", Options);

    private static readonly Regex PipeSegmentJunk = new(
        $@"^(?:{JunkVocabulary})$|karaoke", Options);

    // Matches only the LAST " - "-delimited segment: the character class excludes dash chars, so an
    // earlier dash in the string makes that starting position fail and the engine advances to the
    // final one instead.
    private static readonly Regex TrailingDashSegment = new(@"\s[-–—]\s(?<seg>[^-–—]+)$", Options);

    private static readonly Regex SpacedDashSplit = new(@"^(.+?)\s[-–—]\s(.+)$", Options);

    private static readonly char[] StrayEdgeChars = ['-', '–', '—', '|', '&', ' '];

    public static (string Title, string Artist) Parse(string rawTitle, string channelName = "")
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return (rawTitle, string.Empty);

        var working = rawTitle;
        var artist = string.Empty;

        foreach (var (pattern, artistGroup, titleGroup) in ArtistCarriers)
        {
            var match = pattern.Match(working);
            if (!match.Success) continue;

            artist = Tidy(match.Groups[artistGroup].Value);

            // A trailing space keeps the surviving text off whatever follows the match (a junk
            // bracket, say) — without it "Rhapsody" and "(Karaoke...)" would fuse into one word.
            var replacement = titleGroup is int group ? match.Groups[group].Value + " " : " ";
            working = working[..match.Index] + replacement + working[(match.Index + match.Length)..];
            break;
        }

        var (stripped, pipeArtist) = StripJunk(working, channelName, artist.Length > 0);
        working = stripped;

        if (artist.Length == 0 && pipeArtist.Length > 0)
        {
            artist = Tidy(pipeArtist);
        }
        else if (artist.Length == 0)
        {
            var split = SpacedDashSplit.Match(working);
            if (split.Success)
            {
                var titleFirst = channelName.Length > 0
                    && TitleFirstChannels.Any(c => channelName.Contains(c, StringComparison.OrdinalIgnoreCase));

                if (titleFirst)
                {
                    working = split.Groups[1].Value;
                    artist = Tidy(split.Groups[2].Value);
                }
                else
                {
                    artist = Tidy(split.Groups[1].Value);
                    working = split.Groups[2].Value;
                }
            }
        }

        var title = Tidy(working);

        // A title that dissolves entirely into junk ("Karaoke Version") has nothing left to show;
        // the raw string is a better result than an empty one.
        if (title.Length == 0)
            return (rawTitle, artist);

        return (title, artist);
    }

    private static (string Working, string PipeArtist) StripJunk(string working, string channelName, bool artistAlreadyFound)
    {
        working = BracketedJunk.Replace(working, " ");
        var pipeArtist = string.Empty;

        if (working.Contains('|'))
        {
            var segments = working.Split('|')
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0 && !PipeSegmentJunk.IsMatch(segment))
                .ToList();

            // Drop channel-overlap segments only when something survives — erasing the sole survivor
            // for overlapping the channel would erase the whole title, not just decoration.
            var withoutChannel = segments.Where(segment => !OverlapsChannel(segment, channelName)).ToList();
            if (withoutChannel.Count > 0)
                segments = withoutChannel;

            if (segments.Count == 2 && !artistAlreadyFound)
            {
                // No named carrier claimed the artist, and only two pipe segments are left: YouTube's
                // own "Artist | Title" convention, distinct from KaraFun's spaced-dash "Title - Artist".
                pipeArtist = segments[0];
                working = segments[1];
            }
            else
            {
                working = string.Join(" | ", segments);
            }
        }

        working = StripTrailingChannelSegment(working, channelName);
        working = StripTrailingJunkChain(working);

        return (working, pipeArtist);
    }

    private static string StripTrailingChannelSegment(string working, string channelName)
    {
        if (channelName.Length == 0)
            return working;

        while (true)
        {
            var match = TrailingDashSegment.Match(working);
            if (!match.Success) break;

            var segment = match.Groups["seg"].Value.Trim();
            if (!OverlapsChannel(segment, channelName)) break;

            var remainder = working[..match.Index].TrimEnd();
            if (remainder.Length == 0) break; // never drop the only content left

            working = remainder;
        }

        return working;
    }

    private static string StripTrailingJunkChain(string working)
    {
        while (true)
        {
            var match = TrailingJunkLink.Match(working);
            if (!match.Success) break;

            working = working[..match.Index];
        }

        return working;
    }

    private static bool OverlapsChannel(string segment, string channelName)
        => channelName.Length > 0
            && segment.Length >= 3
            && (channelName.Contains(segment, StringComparison.OrdinalIgnoreCase)
                || segment.Contains(channelName, StringComparison.OrdinalIgnoreCase));

    private static string Tidy(string value)
    {
        var trimmed = value.Trim().Trim('"', '“', '”', '\'', '‘', '’');
        trimmed = Regex.Replace(trimmed, @"\s+", " ");

        return trimmed.Trim(StrayEdgeChars);
    }
}
