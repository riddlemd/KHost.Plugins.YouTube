using KHost.Abstractions.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTube.Tests;

/// <summary>PrepareAsync carries the startup branch logic since InitializeAsync only runs it on
/// a background task nothing can await; every branch here uses a configured path, no PATH or net.</summary>
public class YouTubePluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"khost-ytplugin-{Guid.NewGuid():N}");
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();

    public YouTubePluginTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task PrepareAsync_YtDlpCannotBeResolved_WarnsItCouldNotBePrepared()
    {
        // A configured path that does not exist is an error the resolver raises rather than quietly
        // downloading over, and the plugin turns it into a line the host can act on.
        var resolver = new YtDlpResolver(configuredPath: Path.Combine(_dir, "missing"), toolsDirectory: _dir);

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("could not be prepared")));
    }

    [Fact]
    public async Task PrepareAsync_AProvidedYtDlpThePluginDoesNotOwn_WarnsNothing()
    {
        // A yt-dlp the host installed lives outside the plugin's tools directory, so it is neither
        // the plugin's to warn about nor to update.
        var provided = Path.Combine(_dir, "provided-yt-dlp");
        File.WriteAllText(provided, "");
        var resolver = new YtDlpResolver(configuredPath: provided, toolsDirectory: Path.Combine(_dir, "tools"));

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.DidNotReceiveWithAnyArgs().AddWarning(default!);
    }

    [Fact]
    public async Task PrepareAsync_ADownloadedCopy_WarnsItIsSlowOnMacOsOnly()
    {
        // A copy under the tools directory is one the plugin fetched. AutoUpdate is off so nothing
        // tries to run the placeholder file as 'yt-dlp -U'.
        var toolsDir = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(toolsDir);
        var owned = Path.Combine(toolsDir, YtDlpResolver.ExecutableName);
        File.WriteAllText(owned, "");
        var resolver = new YtDlpResolver(configuredPath: owned, toolsDirectory: toolsDir);

        await YouTubePlugin.PrepareAsync(
            resolver, new YouTubeSettings { AutoUpdate = false }, _context, NullLogger<YouTubePlugin>.Instance);

        if (OperatingSystem.IsMacOS())
            _context.Received(1).AddWarning(Arg.Is<string>(m => m.Contains("brew install yt-dlp")));
        else
            _context.DidNotReceiveWithAnyArgs().AddWarning(default!);
    }

    [Fact]
    public async Task PrepareAsync_TheDownloadCannotConnect_WarnsInPlainWordsWithoutTheExceptionText()
    {
        var resolver = new YtDlpResolver(
            null, Path.Combine(_dir, "tools"), pathVariable: "", handler: new FakeHandler(throws: true));

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning(
            "yt-dlp could not be downloaded — no internet connection. YouTube search will not work until it is.");
    }

    [Fact]
    public async Task PrepareAsync_TheDownloadFailsForAnotherReason_WarnsGenericallyWithoutTheExceptionText()
    {
        // A tampered download is not an offline problem, so it must not be worded as one.
        var resolver = new YtDlpResolver(
            null, Path.Combine(_dir, "tools"), pathVariable: "", handler: new FakeHandler(corruptSums: true));

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning("yt-dlp could not be prepared. The log has the reason.");
    }

    [Fact]
    public async Task PrepareAsync_TheConfiguredPathIsMissing_WarnsWithThePathItWasGiven()
    {
        var missing = Path.Combine(_dir, "missing");
        var resolver = new YtDlpResolver(configuredPath: missing, toolsDirectory: _dir);

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning(Arg.Is<string>(m => m.StartsWith("yt-dlp could not be prepared:") && m.Contains(missing)));
    }

    [Fact]
    public async Task PrepareAsync_TheUpdateCannotConnect_WarnsNoInternetAndKeepsTheStderrOut()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script

        var resolver = OwnedScript("echo 'ERROR: Unable to download API page: Failed to establish a new connection' >&2; exit 1");

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning(
            "yt-dlp could not check for updates — no internet connection; the installed copy is still used.");
    }

    [Fact]
    public async Task PrepareAsync_TheUpdateFailsForAnotherReason_WarnsGenericallyAndKeepsTheStderrOut()
    {
        if (OperatingSystem.IsWindows()) return;

        var resolver = OwnedScript("echo 'ERROR: disk exploded' >&2; exit 1");

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).AddWarning(
            "yt-dlp could not check for updates; the installed copy is still used. The log has the reason.");
    }

    [Fact]
    public async Task PrepareAsync_TheUpdateNeverFinishes_StopsItAndWarnsItTookTooLong()
    {
        if (OperatingSystem.IsWindows()) return;

        var resolver = OwnedScript("sleep 30");

        await YouTubePlugin.PrepareAsync(
            resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance,
            updateTimeout: TimeSpan.FromMilliseconds(400));

        _context.Received(1).AddWarning(
            "yt-dlp could not check for updates — it took too long; the installed copy is still used.");
    }

    [Fact]
    public async Task PrepareAsync_TheUpdateSucceeds_WarnsNothingAboutIt()
    {
        if (OperatingSystem.IsWindows()) return;

        var resolver = OwnedScript("echo up to date");

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.DidNotReceive().AddWarning(Arg.Is<string>(m => m.Contains("update")));
    }

    /// <summary>A copy under the tools directory, so it is the plugin's to update, that runs the script.</summary>
    private YtDlpResolver OwnedScript(string body)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("a shell script cannot stand in for yt-dlp.exe");

        var tools = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(tools);
        var path = Path.Combine(tools, YtDlpResolver.ExecutableName);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new YtDlpResolver(configuredPath: path, toolsDirectory: tools);
    }

    private sealed class FakeHandler(bool throws = false, bool corruptSums = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throws) throw new HttpRequestException("network is down");

            var body = request.RequestUri!.AbsoluteUri.Contains("SHA2-512SUMS", StringComparison.Ordinal)
                ? $"{(corruptSums ? new string('0', 128) : Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes("x"))))}  {YtDlpResolver.AssetName}\n"
                : "x";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch */ }
        GC.SuppressFinalize(this);
    }
}
