using System.Globalization;

namespace KHost.Plugins.YouTube;

/// <summary>bv+ba mode runs percent 0-100 twice (video, audio); a fixed 0.5 split stops audio's
/// 0% reading as regress. A single-stream download reaches only 0.5 here; the provider adds 1.0.</summary>
public static class YtDlpProgressParser
{
    private const string DestinationPrefix = "[download] Destination:";

    /// <summary>Feeds one line plus how many destination markers seen so far (0 initially).
    /// Returns the count to pass next, and the fraction, null when the line carries no progress.</summary>
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

    /// <summary>Matches "[download]  42.3% of ..." and "[download] 100% of ..."; leading whitespace
    /// varies with padding, so this looks for digits before '%' rather than a fixed column.</summary>
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
