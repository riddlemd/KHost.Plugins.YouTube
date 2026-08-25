using System.Diagnostics;
using System.Text;

namespace KHost.Plugins.YouTube;

/// <summary>
/// Runs yt-dlp and hands back its stdout. The seam tests use instead of a real binary. The
/// optional callback fires once per stdout line as it arrives, letting a caller watch a download's
/// progress rather than only seeing the output once the process exits.
/// </summary>
public delegate Task<string> YtDlpRunner(
    IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<string>? onLine = null);

/// <summary>Resolves the binary once, then runs it a process at a time.</summary>
public sealed class YtDlp
{
    private readonly YtDlpResolver _resolver;

    public YtDlp(YtDlpResolver resolver) => _resolver = resolver;

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, Action<string>? onLine = null)
    {
        var executable = await _resolver.ResolveAsync(cancellationToken);

        return await RunProcessAsync(executable, arguments, cancellationToken, onLine);
    }

    private static async Task<string> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<string>? onLine)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList, never a joined string: a song title carrying a quote would otherwise end up
        // being parsed as arguments.
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start yt-dlp at '{executable}'.");

        try
        {
            // Both pipes drained at once. Reading one to the end first deadlocks as soon as the other
            // fills its buffer — the same trap the host's ffmpeg wrapper documents.
            var standardOutput = ReadLinesAsync(process.StandardOutput, onLine, cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(standardOutput, standardError);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"yt-dlp exited with {process.ExitCode}: {standardError.Result.Trim()}");

            return standardOutput.Result;
        }
        catch (OperationCanceledException)
        {
            // yt-dlp spawns ffmpeg to merge the bv+ba streams it downloads separately; a plain Kill
            // leaves that ffmpeg process orphaned and still writing the destination file.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            throw;
        }
    }

    /// <summary>Reads stdout line by line so a caller can observe it as it arrives, while still
    /// handing back the full text once the process ends.</summary>
    private static async Task<string> ReadLinesAsync(
        StreamReader reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var first = true;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!first) builder.Append('\n');
            builder.Append(line);
            first = false;

            onLine?.Invoke(line);
        }

        return builder.ToString();
    }
}
