using System.Net.Sockets;
using KHost.Abstractions.Exceptions;

namespace KHost.Plugins.YouTube;

/// <summary>yt-dlp ran longer than it was allowed and was killed, tree and all.</summary>
public sealed class YtDlpTimeoutException : TimeoutException
{
    public YtDlpTimeoutException(TimeSpan limit)
        : base($"yt-dlp did not finish within {limit.TotalSeconds:0} seconds and was stopped.")
    {
    }
}

/// <summary>There is no yt-dlp to run and the fetch for one failed (or is being left alone for a
/// while); the cause rides along for the log.</summary>
public sealed class YtDlpUnavailableException : Exception
{
    public YtDlpUnavailableException(Exception cause)
        : base("yt-dlp is not installed and could not be downloaded.", cause)
    {
    }
}

/// <summary>Turns what went wrong into a plain line a host can act on. The raw exception is for
/// the log: yt-dlp's stderr names URLs and stack frames nobody at a venue wants to read.</summary>
internal static class YtDlpFailure
{
    public const string OutdatedCode = "KH-YOUTUBE-YTDLP-OUTDATED";

    public const string Unreachable = "YouTube could not be reached. Check this computer is online, then try again.";

    /// <summary>What yt-dlp prints to stderr when the network is the problem.</summary>
    private static readonly string[] ConnectionSignatures =
    [
        "Failed to establish a new connection",
        "Unable to download API page",
        "getaddrinfo",
        "Name or service not known",
        "nodename nor servname",
    ];

    public static bool IsConnection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // A yt-dlp timeout is a TimeoutException too, but it says nothing about the network.
            if (current is YtDlpTimeoutException) return false;

            if (current is HttpRequestException or SocketException or TimeoutException)
                return true;

            if (ConnectionSignatures.Any(s => current.Message.Contains(s, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>One sentence, no stderr. Safe to flash and to hand FailImportAsync.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        YtDlpUnavailableException =>
            "yt-dlp is not installed and could not be downloaded. Check this computer is online, then try again.",
        YtDlpTimeoutException =>
            "yt-dlp took too long and was stopped. Try again.",
        KHostException { ReferenceCode: OutdatedCode } =>
            "yt-dlp on this machine looks too old. Run 'yt-dlp -U' to update it.",
        FileNotFoundException =>
            "yt-dlp was not found at its configured path. Check the yt-dlp Path setting.",
        _ when IsConnection(exception) => Unreachable,
        _ => "yt-dlp could not fetch it.",
    };

    /// <summary>The last line yt-dlp complained about, trimmed, for a place a host reads on purpose
    /// (the Downloads page); null when the failure is already fully described by <see cref="Describe"/>.</summary>
    public static string? Detail(Exception exception)
    {
        if (exception is not InvalidOperationException || IsConnection(exception)) return null;

        var message = exception.Message;
        var marker = message.LastIndexOf("ERROR:", StringComparison.Ordinal);

        // yt-dlp's own verdict is its last ERROR line; with none, the message tail is all there is.
        var line = (marker >= 0 ? message[(marker + "ERROR:".Length)..] : message)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return null;

        return line.Length <= 150 ? line : line[..150] + "…";
    }
}
