using System.Text;
using System.Text.RegularExpressions;

namespace KHost.Plugins.YouTube;

/// <summary>Splits a YouTube title into a song title and artist from known conventions.</summary>
public static class YouTubeTitleParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Shared by the bracket and trailing-word strippers; \b guards "cc" so it never matches inside a
    // real word (YouTube's [CC] closed-caption marker).
    private const string JunkVocabulary =
        @"karaoke(?:\s+version)?|instrumental|lyric\s+video|lyrics?|with\s+lyrics|lyrics\s+on\s+screen|"
        + @"backing\s+track|official\s+(?:video|audio|music\s+video)|hd|hq|4k|\d{3,4}p(?:\d{2})?|"
        + @"sing\s+along|minus\s+one|"
        + @"no\s+lead\s+vocal|guide\s+vocal|\bcc\b";

    // Corpus-derived: channels here use "Title - Artist" on >=90% of graded rows; an absent channel
    // parses backwards until added. "Piano Karaoke" stays out; it also substring-matches Sing2Piano.
    private static readonly string[] TitleFirstChannels =
    [
        "karafun", "easykaraoke", "edkara", "mrentertainerkaraoke", "acoustic lounge",
        "musisi karaoke", "atomic karaoke", "theo's music", "combojam", "sam backing tracks",
        "karaokejp", "mic magic karaoke",
    ];

    // First match wins, so the specific "X by Y" pattern runs only once nothing named the artist.
    // TitleGroup is null for bracket carriers (decoration only); set when the match spans title text too.
    private static readonly (Regex Pattern, int ArtistGroup, int? TitleGroup)[] ArtistCarriers =
    [
        (new Regex(@"[\(\[]\s*in\s+the\s+style\s+of\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        // Stingray Karaoke quotes the artist with no enclosing brackets ("...in the Style of "Artist"...").
        // Requiring the quotes separates it from unquoted "in the style of Artist" rows, left unrecoverable.
        (new Regex("""in\s+the\s+style\s+of\s+["“]([^"”]+)["”]""", Options), 1, null),
        (new Regex(@"[\(\[]\s*originally\s+performed\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*made\s+popular\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex(@"[\(\[]\s*as\s+made\s+famous\s+by\s+([^\)\]]+?)\s*[\)\]]", Options), 1, null),
        (new Regex("""["“]([^"”]+)["”]\s+by\s+([^\(\)\[\]|]+)""", Options), 2, 1),
    ];

    // A bracket can chain multiple junk phrases via "with", another connector, or nothing at all
    // ("HD Karaoke Instrumental"); the connector between phrases is optional, not just "with".
    private const string JunkConnector = @"\s*(?:with|and|&|/|,)?\s*";

    private static readonly Regex BracketedJunk = new(
        $@"[\(\[]\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*[\)\]]", Options);

    // BracketedJunk above requires the WHOLE bracket to reduce to junk, leaving a mixed bracket like
    // "(Karaoke Version with Harmony)" untouched; this drops the bracket instead of keeping "with Harmony".
    private static readonly Regex MixedJunkBracket = new(
        $@"[\(\[]\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s+with\s+[^()\[\]]+?\s*[\)\]]"
        + $@"|[\(\[]\s*[^()\[\]]+?\s+with\s+(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*[\)\]]",
        Options);

    // Peels one trailing junk phrase at a time (looped in StripTrailingJunkChain), so a chain like
    // "- Karaoke Instrumental Lyrics" comes off word by word; the connector is optional for a bare match.
    private static readonly Regex TrailingJunkLink = new(
        $@"(?:\s*[-–—&|])?\s*(?:with\s+)?(?:{JunkVocabulary})\s*$", Options);

    // Channel branding after "from" is not junk vocabulary, so the chain above stops short of it.
    // This drops the "from X" tail only when a junk phrase introduces it, leaving a real title intact.
    private static readonly Regex TrailingBranding = new(
        $@"(?:\s*[-–—&|])?\s*(?:with\s+)?(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*"
        + @"\s+from\s+[^-–—|\(\)\[\]]+$", Options);

    // Decoration fenced by emoji or a bracketed tag ("🎤HQ Karaoke🎤", "[UVR]") is not a connector the
    // chain above matches. The surrogate-pair alternative covers emoji \p{So} misses above the BMP.
    private const string Fence = @"(?:\p{So}|[\uD800-\uDBFF][\uDC00-\uDFFF]|[\[\]])";

    private static readonly Regex TrailingFencedJunk = new(
        $@"\s*{Fence}\s*(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*\s*{Fence}?\s*$",
        Options);

    // A segment is junk only when EVERY word in it is junk vocabulary, not merely because "karaoke"
    // appears somewhere in it, or "The Steve Miller Band Karaoke Version" would be deleted too.
    private static readonly Regex PipeSegmentJunk = new(
        $@"^(?:{JunkVocabulary})(?:{JunkConnector}(?:{JunkVocabulary}))*$", Options);

    // Separate list from TitleFirstChannels on purpose: a channel can use one convention for "-"
    // titles and the other for "|" titles (Vocal Star Karaoke's dash rows are Artist-first, pipe rows not).
    private static readonly string[] PipeTitleFirstChannels =
        ["theo's music", "vocal star karaoke", "sing2piano"];

    // Matches only the LAST " - "-delimited segment: the character class excludes dash chars, so an
    // earlier dash fails and the engine advances to the final one. "•"/"·" need no word-boundary guard.
    private static readonly Regex TrailingDashSegment = new(@"\s[-–—•·]\s(?<seg>[^-–—•·]+)$", Options);

    private static readonly Regex SpacedDashSplit = new(@"^(.+?)\s[-–—•·]\s(.+)$", Options);

    private static readonly char[] StrayEdgeChars = ['-', '–', '—', '•', '·', '|', '&', ' '];

    /// <summary>How the artist was arrived at, which is what says whether it can be trusted.</summary>
    public enum ArtistSource
    {
        /// <summary>No artist found.</summary>
        None,

        /// <summary>The title named it: a stated carrier, a pipe segment, or a title-first channel.</summary>
        Stated,

        /// <summary>Split on a dash and assumed Artist-first. A coin toss on an unknown channel.</summary>
        Guessed,
    }

    public static (string Title, string Artist) Parse(string rawTitle, string channelName = "")
    {
        var (title, artist, _) = ParseDetailed(rawTitle, channelName);
        return (title, artist);
    }

    /// <summary>Parses every result of one search together, so a title stating the artist outright
    /// settles the orientation for rows that only have a dash to go on.</summary>
    public static IReadOnlyList<(string Title, string Artist)> ParseAll(
        IReadOnlyList<(string RawTitle, string ChannelName)> results)
    {
        var parsed = results.Select(r => ParseDetailed(r.RawTitle, r.ChannelName)).ToList();

        var stated = parsed
            .Where(p => p.Source == ArtistSource.Stated && p.Artist.Length > 0)
            .Select(p => Fold(p.Artist))
            .ToHashSet(StringComparer.Ordinal);

        var anchors = new HashSet<string>(stated, StringComparer.Ordinal);

        // With no stated anchor, the set votes: the name landing in the artist slot most often is the
        // one most channels agree on. Two occurrences is the floor; one row agreeing is not evidence.
        var modal = parsed
            .Where(p => p.Artist.Length > 0)
            .GroupBy(p => Fold(p.Artist), StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        if (modal is not null)
            anchors.Add(modal.Key);

        if (anchors.Count == 0)
            return [.. parsed.Select(p => (p.Title, p.Artist))];

        return
        [
            .. parsed.Select(p => ShouldSwap(p, anchors)
                    ? (p.Artist, p.Title)
                    : (p.Title, p.Artist))
        ];
    }

    /// <summary>A guessed row is backwards if its title is a known artist and its artist is not.</summary>
    /// <remarks>Stated and modal anchors weigh equally; ranking stated above the vote scored worse.</remarks>
    private static bool ShouldSwap(
        (string Title, string Artist, ArtistSource Source) parsed,
        IReadOnlySet<string> anchors)
    {
        if (parsed.Source != ArtistSource.Guessed) return false;

        return anchors.Contains(Fold(parsed.Title))
            && !anchors.Contains(Fold(parsed.Artist));
    }

    /// <summary>Case, spacing and punctuation are all noise when comparing two names.</summary>
    private static string Fold(string value)
    {
        var folded = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                folded.Append(char.ToLowerInvariant(character));
        }

        return folded.ToString();
    }

    public static (string Title, string Artist, ArtistSource Source) ParseDetailed(string rawTitle, string channelName = "")
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return (rawTitle, string.Empty, ArtistSource.None);

        var working = rawTitle;
        var artist = string.Empty;

        foreach (var (pattern, artistGroup, titleGroup) in ArtistCarriers)
        {
            var match = pattern.Match(working);
            if (!match.Success) continue;

            artist = Tidy(match.Groups[artistGroup].Value);

            // A trailing space keeps the surviving text off whatever follows the match (a junk
            // bracket, say); without it "Rhapsody" and "(Karaoke...)" would fuse into one word.
            var replacement = titleGroup is int group ? match.Groups[group].Value + " " : " ";
            working = working[..match.Index] + replacement + working[(match.Index + match.Length)..];
            break;
        }

        var source = artist.Length > 0 ? ArtistSource.Stated : ArtistSource.None;

        var (stripped, pipeArtist) = StripJunk(working, channelName, artist.Length > 0);
        working = stripped;

        if (artist.Length == 0 && pipeArtist.Length > 0)
        {
            artist = Tidy(pipeArtist);
            source = ArtistSource.Stated;
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

                    // A listed channel is an observed convention, not a coin toss, so this one is
                    // not up for reconsideration by the rest of the result set.
                    source = ArtistSource.Stated;
                }
                else
                {
                    artist = Tidy(split.Groups[1].Value);
                    working = split.Groups[2].Value;
                    source = ArtistSource.Guessed;
                }
            }
        }

        var title = Tidy(working);

        // A title that dissolves entirely into junk ("Karaoke Version") has nothing left to show;
        // the raw string is a better result than an empty one.
        if (title.Length == 0)
            return (rawTitle, artist, source);

        return (title, artist, source);
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

            // Drop channel-overlap segments only when something survives. Erasing the sole survivor
            // for overlapping the channel would erase the whole title, not just decoration.
            var withoutChannel = segments.Where(segment => !OverlapsChannel(segment, channelName)).ToList();
            if (withoutChannel.Count > 0)
                segments = withoutChannel;

            // A segment can carry real content plus its own trailing junk suffix ("The Joker Karaoke")
            // rather than being pure junk outright; peel that per segment, same as the whole title.
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
                    // "Title Karaoke | Artist Karaoke Version" is Vocal Star Karaoke's and Sing2Piano's
                    // house style, the reverse of YouTube's own "Artist | Title" convention below.
                    working = segments[0];
                    pipeArtist = segments[1];
                }
                else
                {
                    // No named carrier claimed the artist and only two pipe segments remain: this is
                    // YouTube's own "Artist | Title" convention, distinct from the spaced-dash one.
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

    // A pipe segment can be channel self-promotion built AROUND the channel name rather than matching
    // it outright ("Vocal-Star Karaoke" for "Vocal Star Karaoke"); fold hyphen/space before the check.
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

        // A segment mentioning the channel name is, in the corpus, always the channel's own trailing
        // credit ("NOX Karaoke (with background vocals)"), so no length cutoff is needed here.
        return segment.Contains(channelName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Tidy(string value)
    {
        var trimmed = value.Trim().Trim('"', '“', '”', '\'', '‘', '’');
        trimmed = Regex.Replace(trimmed, @"\s+", " ");

        return trimmed.Trim(StrayEdgeChars);
    }
}
