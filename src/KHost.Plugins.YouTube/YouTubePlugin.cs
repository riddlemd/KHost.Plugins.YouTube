using KHost.Plugins.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTube;

/// <summary>
/// Settles yt-dlp before the first search rather than during it: resolving it may mean a 35MB
/// download, and the host who triggers that should not be the one waiting on it mid-shift.
/// </summary>
public sealed class YouTubePlugin : IPlugin
{
    private const string SlowOnMacOs =
        "yt-dlp was downloaded rather than found on this machine. On macOS that build is rescanned "
        + "on every launch, which costs seconds on every search — 'brew install yt-dlp' avoids it.";

    private readonly ILogger<YouTubePlugin> _logger;

    // Resolved from the host's container: the loader builds plugins with ActivatorUtilities, so a
    // constructor parameter the host can supply is simply handed over.
    public YouTubePlugin(ILogger<YouTubePlugin> logger) => _logger = logger;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var settings = context.BindSettings<YouTubeSettings>();

        var resolver = new YtDlpResolver(
            settings.YtDlpPath,
            Path.Combine(AppContext.BaseDirectory, "cache", "tools"));

        // Started, not awaited: a first run fetches 35MB, and startup is a window the host is
        // watching. Nothing else waits on this — a search resolves yt-dlp for itself either way.
        _ = Task.Run(() => PrepareAsync(resolver, settings, context, _logger), CancellationToken.None);

        return Task.CompletedTask;
    }

    private static async Task PrepareAsync(
        YtDlpResolver resolver, YouTubeSettings settings, IPluginContext context, ILogger logger)
    {
        string executable;

        try
        {
            executable = await resolver.ResolveAsync();

            logger.LogInformation("yt-dlp resolved to {Path}", executable);
        }
        catch (Exception ex)
        {
            // Reported rather than thrown: searching is what fails, and it can say so itself with
            // the query in hand. This only explains it in advance.
            context.ReportWarning($"yt-dlp could not be prepared: {ex.Message}");
            return;
        }

        if (!resolver.OwnsCopyAt(executable))
            return;

        if (OperatingSystem.IsMacOS())
            context.ReportWarning(SlowOnMacOs);

        if (!settings.AutoUpdate)
            return;

        try
        {
            await new YtDlp(resolver).RunAsync(["-U"]);
        }
        catch (Exception ex)
        {
            // The version already on disk still works, so this is worth saying and not worth failing.
            context.ReportWarning($"yt-dlp could not be updated: {ex.Message}");
        }
    }
}
