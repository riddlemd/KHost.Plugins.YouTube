using System.Diagnostics;

namespace KHost.Plugin.YouTube;

/// <summary>Runs yt-dlp and hands back its stdout. The seam tests use instead of a real binary.</summary>
public delegate Task<string> YtDlpRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>Resolves the binary once, then runs it a process at a time.</summary>
public sealed class YtDlp
{
    private readonly YtDlpResolver _resolver;

    public YtDlp(YtDlpResolver resolver) => _resolver = resolver;

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var executable = await _resolver.ResolveAsync(cancellationToken);

        return await RunProcessAsync(executable, arguments, cancellationToken);
    }

    private static async Task<string> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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

        // Both pipes drained at once. Reading one to the end first deadlocks as soon as the other
        // fills its buffer — the same trap the host's ffmpeg wrapper documents.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(standardOutput, standardError);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"yt-dlp exited with {process.ExitCode}: {standardError.Result.Trim()}");

        return standardOutput.Result;
    }
}
