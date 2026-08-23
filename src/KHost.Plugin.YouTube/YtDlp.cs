using System.Diagnostics;

namespace KHost.Plugin.YouTube;

/// <summary>Runs yt-dlp and hands back its stdout. The seam tests use instead of a real binary.</summary>
public delegate Task<string> YtDlpRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>Resolves the binary once, then runs it a process at a time.</summary>
public sealed class YtDlp
{
    private readonly YtDlpResolver _resolver;
    private readonly bool _autoUpdate;

    private int _updateStarted;

    public YtDlp(YtDlpResolver resolver, bool autoUpdate = true)
    {
        _resolver = resolver;
        _autoUpdate = autoUpdate;
    }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var executable = await _resolver.ResolveAsync(cancellationToken);

        StartUpdateOnce(executable);

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

    /// <summary>
    /// Refreshes our own copy once per run, in the background. YouTube breaks extraction every few
    /// weeks and yt-dlp ships the fix within days, so a host who never thinks about it still gets
    /// one — but a search must not wait on a network round trip to start.
    /// </summary>
    private void StartUpdateOnce(string executable)
    {
        if (!_autoUpdate) return;

        // Only the copy we downloaded. A packaged install refuses -U and says to use its package
        // manager, so running it there is noise at best.
        if (!_resolver.OwnsCopyAt(executable)) return;

        if (Interlocked.Exchange(ref _updateStarted, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunProcessAsync(executable, ["-U"], CancellationToken.None);
            }
            catch
            {
                // An update that cannot run leaves the version we already have, which still works.
            }
        });
    }
}
