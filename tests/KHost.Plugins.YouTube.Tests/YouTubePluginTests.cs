using KHost.Plugins.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTube.Tests;

/// <summary>
/// The plugin's startup decides what a host is told about yt-dlp — nothing, a "could not be
/// prepared" line, or the macOS-is-slow advice. That logic lives in PrepareAsync because
/// InitializeAsync only ever runs it on a background task nothing can await. Every branch here is
/// driven through a configured path, so the resolver never touches PATH or the network.
/// </summary>
public class YouTubePluginTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"khost-ytplugin-{Guid.NewGuid():N}");
    private readonly IPluginContext _context = Substitute.For<IPluginContext>();

    public YouTubePluginTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task PrepareAsync_YtDlpCannotBeResolved_WarnsItCouldNotBePrepared()
    {
        // A configured path that does not exist is an error the resolver raises rather than quietly
        // downloading over — the plugin turns it into a line the host can act on.
        var resolver = new YtDlpResolver(configuredPath: Path.Combine(_dir, "missing"), toolsDirectory: _dir);

        await YouTubePlugin.PrepareAsync(resolver, new YouTubeSettings(), _context, NullLogger<YouTubePlugin>.Instance);

        _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("could not be prepared")));
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

        _context.DidNotReceiveWithAnyArgs().ReportWarning(default!);
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
            _context.Received(1).ReportWarning(Arg.Is<string>(m => m.Contains("brew install yt-dlp")));
        else
            _context.DidNotReceiveWithAnyArgs().ReportWarning(default!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch */ }
        GC.SuppressFinalize(this);
    }
}
