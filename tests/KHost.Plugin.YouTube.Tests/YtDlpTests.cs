using KHost.Plugin.YouTube;
using System.Diagnostics;

namespace KHost.Plugin.YouTube.Tests;

public class YtDlpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ytdlp-treekill-{Guid.NewGuid():N}");

    public YtDlpTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ThrowsOperationCanceled()
    {
        var resolver = new YtDlpResolver("/bin/sleep", _root);
        var ytDlp = new YtDlp(resolver);

        using var cts = new CancellationTokenSource();
        var run = ytDlp.RunAsync(["30"], cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsTheWholeProcessTreeRatherThanJustTheTopProcess()
    {
        var pidFile = Path.Combine(_root, "child.pid");

        // The parent shell backgrounds a child `sleep`, mirroring yt-dlp spawning ffmpeg as a
        // child of its own process — a tree kill has to reach that child, not just the shell.
        var script = $"sleep 30 & echo $! > '{pidFile}'; wait";

        var resolver = new YtDlpResolver("/bin/sh", _root);
        var ytDlp = new YtDlp(resolver);

        using var cts = new CancellationTokenSource();
        var run = ytDlp.RunAsync(["-c", script], cts.Token);

        var childPid = await WaitForChildPidAsync(pidFile);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(await WaitUntilDeadAsync(childPid), "child sleep process survived the cancel");
    }

    private static async Task<int> WaitForChildPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                var text = (await File.ReadAllTextAsync(pidFile)).Trim();
                if (int.TryParse(text, out var pid)) return pid;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("child sleep process never reported its pid");
    }

    private static async Task<bool> WaitUntilDeadAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(pid)) return true;

            await Task.Delay(25);
        }

        return !IsProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
