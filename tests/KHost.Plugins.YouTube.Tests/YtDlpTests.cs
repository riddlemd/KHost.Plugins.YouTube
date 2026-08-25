using KHost.Plugins.YouTube;
using System.Diagnostics;

namespace KHost.Plugins.YouTube.Tests;

public class YtDlpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ytdlp-treekill-{Guid.NewGuid():N}");

    public YtDlpTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // A process this test killed can still hold the directory for a moment on Windows, and a
        // failure to tidy up must not be reported as the test failing.
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Stands in for yt-dlp. A real child process is the point of these tests — what the wrapper
    /// does to one is the whole behaviour — so the process layer cannot be substituted away, and
    /// each platform gets the shell it actually has.
    /// </summary>
    private static class Stub
    {
        // Absolute, because the resolver takes a configured path and rejects one it cannot see on
        // disk — a bare "cmd.exe" resolves against the working directory and is not found.
        private static readonly string Cmd =
            Path.Combine(Environment.SystemDirectory, "cmd.exe");

        private static readonly string PowerShell =
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        public static (string Executable, string[] Arguments) Echo(params string[] lines) =>
            OperatingSystem.IsWindows()
                // No space before the separator: cmd takes one as part of the line it echoes.
                ? (Cmd, ["/c", string.Join("&", lines.Select(line => $"echo {line}"))])
                : ("/bin/sh", ["-c", string.Join("; ", lines.Select(line => $"echo {line}"))]);

        /// <summary>Runs long enough that only cancellation ends it.</summary>
        public static (string Executable, string[] Arguments) Sleep() =>
            OperatingSystem.IsWindows()
                ? (PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"])
                : ("/bin/sleep", ["30"]);

        /// <summary>
        /// Sleeps in a grandchild and reports its id, mirroring yt-dlp spawning ffmpeg: killing
        /// only the top process leaves that one running and still writing its output file.
        /// </summary>
        public static (string Executable, string[] Arguments) SleepInAChild(string pidFile) =>
            OperatingSystem.IsWindows()
                ? (PowerShell,
                    [
                        "-NoProfile", "-Command",
                        "$child = Start-Process powershell "
                        + "-ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' "
                        + "-PassThru -WindowStyle Hidden; "
                        + $"Set-Content -LiteralPath '{pidFile}' -Value $child.Id; "
                        + "Wait-Process -Id $child.Id",
                    ])
                : ("/bin/sh", ["-c", $"sleep 30 & echo $! > '{pidFile}'; wait"]);
    }

    private YtDlp Build((string Executable, string[] Arguments) stub)
        => new(new YtDlpResolver(stub.Executable, _root));

    [Fact]
    public async Task RunAsync_OnLineSupplied_ReceivesEachLineAsItArrivesAndTheFullOutputIsStillReturned()
    {
        var stub = Stub.Echo("one", "two", "three");

        var seen = new List<string>();
        var output = await Build(stub).RunAsync(stub.Arguments, onLine: seen.Add);

        Assert.Equal(["one", "two", "three"], seen);
        Assert.Equal("one\ntwo\nthree", output);
    }

    [Fact]
    public async Task RunAsync_NoOnLineSupplied_StillReturnsFullOutput()
    {
        var stub = Stub.Echo("one", "two");

        var output = await Build(stub).RunAsync(stub.Arguments);

        Assert.Equal("one\ntwo", output);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ThrowsOperationCanceled()
    {
        var stub = Stub.Sleep();

        using var cts = new CancellationTokenSource();
        var run = Build(stub).RunAsync(stub.Arguments, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsTheWholeProcessTreeRatherThanJustTheTopProcess()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var stub = Stub.SleepInAChild(pidFile);

        using var cts = new CancellationTokenSource();
        var run = Build(stub).RunAsync(stub.Arguments, cts.Token);

        var childPid = await WaitForChildPidAsync(pidFile);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(await WaitUntilDeadAsync(childPid), "child sleep process survived the cancel");
    }

    private static async Task<int> WaitForChildPidAsync(string pidFile)
    {
        // Generous: the Windows stub pays for a PowerShell start before it can report anything.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                // The file can be seen between being created and being written, so an empty read
                // is a retry rather than a failure.
                var text = (await ReadAllTextSafelyAsync(pidFile)).Trim();
                if (int.TryParse(text, out var pid)) return pid;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("child sleep process never reported its pid");
    }

    private static async Task<string> ReadAllTextSafelyAsync(string path)
    {
        try { return await File.ReadAllTextAsync(path); }
        catch (IOException) { return string.Empty; }
    }

    private static async Task<bool> WaitUntilDeadAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

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
