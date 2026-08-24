using System.Globalization;

namespace KHost.Plugin.YouTube;

/// <summary>
/// Turns one line of yt-dlp's <c>--newline</c> progress output into a fraction of the whole
/// download. bv+ba mode fetches video then audio sequentially, so raw percent runs 0→100 twice;
/// splitting the range at a fixed 0.5 boundary (stream 1 → [0,0.5], stream 2+ → [0.5,1]) is what
/// keeps the audio stream's 0% from reading as progress running backwards right after the video
/// stream's 100%. The trade-off: a /b-fallback single-stream download only reaches 0.5 through
/// this parser — the provider reports the real 1.0 itself once the file is actually on disk.
/// </summary>
public static class YtDlpProgressParser
{
    private const string DestinationPrefix = "[download] Destination:";

    /// <summary>
    /// Feeds one line of output, given how many "[download] Destination:" lines have appeared so
    /// far (0 before the first). Returns the count to pass to the next line, plus the
    /// overall-download fraction this line represents — null when the line carries no progress
    /// (a destination announcement, an unrelated line, or one that doesn't parse).
    /// </summary>
    public static (int DestinationsSeen, double? Fraction) Parse(int destinationsSeen, string line)
    {
        if (line.StartsWith(DestinationPrefix, StringComparison.Ordinal))
            return (destinationsSeen + 1, null);

        var percent = ParsePercent(line);
        if (percent is null)
            return (destinationsSeen, null);

        var (low, high) = destinationsSeen <= 1 ? (0.0, 0.5) : (0.5, 1.0);

        return (destinationsSeen, low + (percent.Value / 100.0 * (high - low)));
    }

    /// <summary>
    /// Matches "[download]  42.3% of ..." and "[download] 100% of ...". The percent's own leading
    /// whitespace varies with padding, so this looks for digits immediately before a literal '%'
    /// rather than assuming a fixed column.
    /// </summary>
    private static double? ParsePercent(string line)
    {
        if (!line.StartsWith("[download]", StringComparison.Ordinal))
            return null;

        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
            return null;

        var start = percentIndex;
        while (start > 0 && (char.IsAsciiDigit(line[start - 1]) || line[start - 1] == '.'))
            start--;

        return double.TryParse(
            line.AsSpan(start, percentIndex - start),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}
