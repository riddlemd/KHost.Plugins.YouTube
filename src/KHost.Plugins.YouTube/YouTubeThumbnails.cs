using System.Text.Json;

namespace KHost.Plugins.YouTube;

/// <summary>Picks the still a search result is shown with.</summary>
public static class YouTubeThumbnails
{
    /// <summary>
    /// The console draws these small. Anything larger is bytes a host waits on for a picture they
    /// are scanning, not studying, so the narrowest one that still covers the cell wins.
    /// </summary>
    private const int TargetWidth = 360;

    /// <summary>Empty when the result carries no usable image, which leaves the cell blank.</summary>
    public static string Pick(JsonElement root)
    {
        if (!root.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var best = string.Empty;
        var bestWidth = int.MaxValue;

        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            if (!thumbnail.TryGetProperty("url", out var url) || url.GetString() is not { Length: > 0 } address)
                continue;

            var width = thumbnail.TryGetProperty("width", out var w) && w.TryGetInt32(out var value)
                ? value
                : 0;

            // An unsized entry is a last resort: it is taken only while nothing else has been.
            if (best.Length == 0)
            {
                best = address;
                bestWidth = width == 0 ? int.MaxValue : width;
                continue;
            }

            if (width == 0)
                continue;

            var covers = width >= TargetWidth;
            var bestCovers = bestWidth >= TargetWidth;

            // Prefer the smallest that covers the cell; failing that, the largest that does not.
            if ((covers && (!bestCovers || width < bestWidth)) || (!covers && !bestCovers && width > bestWidth))
            {
                best = address;
                bestWidth = width;
            }
        }

        return best;
    }
}
