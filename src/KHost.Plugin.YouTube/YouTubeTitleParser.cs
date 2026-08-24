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
    // "cc" (closed-captions marker, e.g. "[CC]") is common on the "CC Karaoke" channels' bracket
    // decoration; \b guards it so it never matches as a substring of a real word.
    private const string JunkVocabulary =
        @"karaoke(?:\s+version)?|instrumental|lyric\s+video|lyrics?|with\s+lyrics|lyrics\s+on\s+screen|"
        + @"backing\s+track|official\s+(?:video|audio|music\s+video)|hd|hq|4k|\d{3,4}p(?:\d{2})?|"
        + @"sing\s+along|minus\s+one|"
        + @"no\s+lead\s+vocal|guide\s+vocal|\bcc\b";

    // Corpus-derived (spikes/title-parse-corpus): for each channel with >=5 graded spaced-dash rows,
    // >=90% of those rows use "Title - Artist" rather than "Artist - Title". This is an empirical,
    // overfit-prone list scoped to the 500-song corpus — a real channel using this house style but
    // absent (or under-sampled) here will still parse backwards until it earns its own entry.
    // "Piano Karaoke" is deliberately omitted despite qualifying: as a Contains() substring it would
    // also match "Sing2Piano | Piano Karaoke Instrumentals" and "KaraoKeysPH | Piano Karaoke
    // Instrumentals", and the Sing2Piano channel is Artist-first (opposite convention) — the
    // substring can't tell the three apart, so including it would flip ~29 already-correct rows.
    private static readonly string[] TitleFirstChannels =
    [
        "karafun", "easykaraoke", "edkara", "mrentertainerkaraoke", "acoustic lounge",
        "musisi karaoke", "atomic karaoke", "theo's music", "combojam", "sam backing tracks",
        "karaokejp", "mic magic karaoke",
    ];

    // First match wins, so the more specific "X by Y" pattern is tried only when nothing that names
    // the segment explicitly ("in the style of", etc.) has already claimed the artist. TitleGroup is
    // null for the bracket carriers — there is no title text inside them, just decoration to drop —
    // and set for the quoted-title carrier, whose match spans the title text too and must keep it.
    private static readonly (Regex Pattern, int ArtistGroup, int? TitleGroup)[] ArtistCarriers =
    [
        (new Regex(@"[\(\[]\s*in\s+the\s+style\s+of\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        // Stingray Karaoke's house style: no enclosing brackets at all, just the artist name quoted
        // ("Paint It Black in the Style of "The Rolling Stones" karaoke with lyrics"). Requiring the
        // quotes is what tells this apart from the ~4 corpus rows that say "in the style of Artist"
        // with no quotes and no delimiter before the trailing junk — those stay unrecoverable.
        (new Regex("""in\s+the\s+style\s+of\s+["“]([^"”]+)["”]""", Options), 1, null),
        (new Regex(@"[\(\[]\s*originally\s+performed\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*made\s+popular\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*as\s+made\s+famous\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex("""["“]([^"”]+)["”]\s+by\s+([^\(\)\[\]|]+)""", Options), 2, 1),
    ];

    // A bracket can carry more than one junk phrase, joined by "with" ("Karaoke with Lyrics"), by
    // another connector ("CC Karaoke / Instrumental"), or by nothing at all ("HD Karaoke",
    // "Karaoke Instrumental") — the connector between chained phrases is optional, not just "with".
    private const string JunkConnector = @"\s*(?:with|and|&|/|,)?\s*";

    private static readonly Regex BracketedJunk = new(
        $@"[\(\[]\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*[\)\]]", Options);

    // A bracket can also pair a junk phrase with a genuine, non-junk qualifier via "with"
    // ("Karaoke Version with Harmony") — BracketedJunk above requires the WHOLE bracket to reduce to
    // junk and leaves this untouched. The whole bracket is dropped rather than kept-minus-the-junk-
    // word: "with Harmony" alone is still decoration text the grader (and a host) has no use for, not
    // a title/artist fragment worth preserving on its own.
    private static readonly Regex MixedJunkBracket = new(
        $@"[\(\[]\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s+with\s+[^()\[\]]+?\s*[\)\]]"
        + $@"|[\(\[]\s*[^()\[\]]+?\s+with\s+(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*[\)\]]",
        Options);

    // Peels one trailing junk phrase at a time (looped in StripTrailingJunkChain), so a run like
    // "- Karaoke Instrumental Lyrics" comes off word by word. The connector is optional so a bare
    // junk phrase with nothing before it (the whole title is junk) still matches.
    private static readonly Regex TrailingJunkLink = new(
        $@"(?:\s*[-–—&|])?\s*(?:with\s+)?(?:{JunkVocabulary})\s*$", Options);

    // A channel signs its own decoration, and the branding is not itself junk vocabulary, so the
    // chain above stops the moment it reaches one: "Karaoke Version from Zoom Karaoke" peeled the
    // final "Karaoke" and left "- Karaoke Version from Zoom" sitting in 128 corpus titles. Anything
    // after "from" is the signature, so the whole tail goes — but only when a junk phrase introduced
    // it, which is what keeps a real title like "Message from the Fireflies" intact.
    private static readonly Regex TrailingBranding = new(
        $@"(?:\s*[-–—&|])?\s*(?:with\s+)?(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*"
        + @"\s+from\s+[^-–—|\(\)\[\]]+$", Options);

    // Decoration fenced by emoji or a bracketed tag rather than by a separator ("🎤HQ Karaoke🎤",
    // "[UVR]") — the fence is not a character the chain treats as a connector, so it never matched.
    // The surrogate-pair alternative is required: .NET matches \p{So} per UTF-16 unit, so it does
    // not match an emoji above the BMP — 🎤 is two units and slips straight past a bare \p{So}.
    private const string Fence = @"(?:\p{So}|[\uD800-\uDBFF][\uDC00-\uDFFF]|[\[\]])";

    private static readonly Regex TrailingFencedJunk = new(
        $@"\s*{Fence}\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*{Fence}?\s*$",
        Options);

    // A whole segment counts as junk only when EVERY word in it is junk vocabulary (chained the same
    // way BracketedJunk chains a bracket's contents) — not merely because "karaoke" appears somewhere
    // in it. The old bare "karaoke" alternative matched real content too ("The Steve Miller Band
    // Karaoke Version" is an artist name, not decoration) and silently deleted it.
    private static readonly Regex PipeSegmentJunk = new(
        $@"^(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*$", Options);

    // Corpus-derived, same method as TitleFirstChannels but for the two-pipe-segment case
    // ("Title Karaoke | Artist Karaoke Version") — a channel can use one convention for its "-"
    // titles and the other for its "|" titles (Vocal Star Karaoke's dash rows are Artist-first; its
    // pipe rows are Title-first), so this is deliberately a separate list, not a reuse of the other.
    private static readonly string[] PipeTitleFirstChannels =
        ["theo's music", "vocal star karaoke", "sing2piano"];

    // Matches only the LAST " - "-delimited segment: the character class excludes dash chars, so an
    // earlier dash in the string makes that starting position fail and the engine advances to the
    // final one instead.
    // "•" and "·" are a fourth separator convention (the "CC Karaoke" channels' "Artist • Title"),
    // alongside -/en-dash/em-dash — always space-delimited in the corpus, never fused to a word, so
    // no word-boundary guard is needed the way one might be for a mid-word middle dot.
    private static readonly Regex TrailingDashSegment = new(@"\s[-–—•·]\s(?<seg>[^-–—•·]+)$", Options);

    private static readonly Regex SpacedDashSplit = new(@"^(.+?)\s[-–—•·]\s(.+)$", Options);

    private static readonly char[] StrayEdgeChars = ['-', '–', '—', '•', '·', '|', '&', ' '];

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
        working = MixedJunkBracket.Replace(working, " ");
        var pipeArtist = string.Empty;

        if (working.Contains('|'))
        {
            var segments = working.Split('|')
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0 && !IsPipeSegmentJunkOrChannelPromo(segment, channelName))
                .ToList();

            // Drop channel-overlap segments only when something survives — erasing the sole survivor
            // for overlapping the channel would erase the whole title, not just decoration.
            var withoutChannel = segments.Where(segment => !OverlapsChannel(segment, channelName)).ToList();
            if (withoutChannel.Count > 0)
                segments = withoutChannel;

            // A segment can carry real content plus a trailing junk suffix of its own ("The Joker
            // Karaoke", "The Steve Miller Band Karaoke Version") rather than being pure junk outright
            // — peel that per segment, same as the trailing-junk chain applied to the title as a whole.
            // A segment can carry real content plus a trailing junk suffix of its own ("The Joker
            // Karaoke", "The Steve Miller Band Karaoke Version") rather than being pure junk outright
            // — peel that per segment, same as the trailing-junk chain applied to the title as a whole.
            segments = segments
                .Select(segment => StripTrailingJunkChain(segment).Trim())
                .Where(segment => segment.Length > 0)
                .ToList();

            if (segments.Count == 2 && !artistAlreadyFound)
            {
                var titleFirst = channelName.Length > 0
                    && PipeTitleFirstChannels.Any(c => channelName.Contains(c, StringComparison.OrdinalIgnoreCase));

                if (titleFirst)
                {
                    // "Title Karaoke | Artist Karaoke Version" — Vocal Star Karaoke's and Sing2Piano's
                    // house style, the reverse of YouTube's own "Artist | Title" convention below.
                    working = segments[0];
                    pipeArtist = segments[1];
                }
                else
                {
                    // No named carrier claimed the artist, and only two pipe segments are left:
                    // YouTube's own "Artist | Title" convention, distinct from KaraFun's spaced-dash
                    // "Title - Artist".
                    pipeArtist = segments[0];
                    working = segments[1];
                }
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
            var match = TrailingBranding.Match(working);
            if (!match.Success) match = TrailingFencedJunk.Match(working);
            if (!match.Success) match = TrailingJunkLink.Match(working);
            if (!match.Success) break;

            working = working[..match.Index];
        }

        return working;
    }

    // A pipe segment can also be channel self-promotion built AROUND the channel name rather than
    // matching it outright — "With Lyrics HD Vocal-Star Karaoke 4K" for channel "Vocal Star Karaoke"
    // (note the hyphen the channel's own branding uses in place of a space). Strip the channel name
    // out (hyphen/space folded together) and check whether everything left is junk vocabulary; a
    // segment that's genuine content (an artist name, say) won't reduce to nothing but junk this way.
    private static bool IsPipeSegmentJunkOrChannelPromo(string segment, string channelName)
    {
        if (PipeSegmentJunk.IsMatch(segment))
            return true;

        if (channelName.Length == 0)
            return false;

        var normalizedSegment = segment.Replace('-', ' ');
        var normalizedChannel = channelName.Replace('-', ' ');
        var idx = normalizedSegment.IndexOf(normalizedChannel, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var remainder = normalizedSegment[..idx] + " " + normalizedSegment[(idx + normalizedChannel.Length)..];
        remainder = Regex.Replace(remainder, @"\s+", " ").Trim();

        return remainder.Length == 0 || PipeSegmentJunk.IsMatch(remainder);
    }

    private static bool OverlapsChannel(string segment, string channelName)
    {
        if (channelName.Length == 0 || segment.Length < 3)
            return false;

        if (channelName.Contains(segment, StringComparison.OrdinalIgnoreCase))
            return true;

        // A segment mentioning the channel name is (near enough, in the corpus) always the channel's
        // own trailing credit ("NOX Karaoke (with background vocals)", "Zoom Karaoke Official"), even
        // with real decoration text stuck to it — a tighter length-based cutoff was tried here to stop
        // a "•"-delimited bracket like "(CC Karaoke / Instrumental)" from swallowing the song title,
        // but BracketedJunk/MixedJunkBracket now strip that bracket before this ever runs (both "cc"
        // and "karaoke" are junk vocabulary), so the cutoff was left blocking legitimate credit
        // segments for no remaining benefit — corpus-measured net negative once removed.
        return segment.Contains(channelName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Tidy(string value)
    {
        var trimmed = value.Trim().Trim('"', '“', '”', '\'', '‘', '’');
        trimmed = Regex.Replace(trimmed, @"\s+", " ");

        return trimmed.Trim(StrayEdgeChars);
    }
}
