using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.YouTube;

/// <summary>Settles yt-dlp before the first search rather than during it: resolving it may mean
/// a 35MB download, and the host who triggers that should not be the one waiting mid-shift.</summary>
public sealed class YouTubePlugin : IPlugin
{
    private const string SlowOnMacOs =
        "yt-dlp was downloaded rather than found on this machine. On macOS that build is rescanned "
        + "on every launch, which costs seconds on every search — 'brew install yt-dlp' avoids it.";

    /// <summary>yt-dlp -U fetches a 35MB binary and swaps it in; five minutes covers a poor venue
    /// link, while a process that never ends would otherwise sit in the background for the whole show.</summary>
    internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);

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
        // watching. Nothing else waits on this, since a search resolves yt-dlp for itself either way.
        _ = Task.Run(() => PrepareAsync(resolver, settings, context, _logger), CancellationToken.None);

        return Task.CompletedTask;
    }

    // Internal, not private: what warning a host is shown (missing, provided, or downloaded)
    // is the branch worth a test, and InitializeAsync only fires it on a task nothing can await.
    internal static async Task PrepareAsync(
        YtDlpResolver resolver,
        YouTubeSettings settings,
        IPluginContext context,
        ILogger logger,
        TimeSpan? updateTimeout = null)
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
            logger.LogWarning(ex, "yt-dlp could not be prepared");
            context.AddWarning(DescribePrepareFailure(ex));
            return;
        }

        if (!resolver.OwnsCopyAt(executable))
            return;

        if (OperatingSystem.IsMacOS())
            context.AddWarning(SlowOnMacOs);

        if (!settings.AutoUpdate)
            return;

        try
        {
            await new YtDlp(resolver).RunAsync(["-U"], timeout: updateTimeout ?? UpdateTimeout);
        }
        catch (Exception ex)
        {
            // The version already on disk still works, so this is worth saying and not worth failing.
            logger.LogWarning(ex, "yt-dlp could not be updated");
            context.AddWarning(DescribeUpdateFailure(ex));
        }
    }

    // Plain words only: the exception text is HttpRequestException or yt-dlp's stderr, for the log.
    private static string DescribePrepareFailure(Exception ex) => ex switch
    {
        FileNotFoundException or PlatformNotSupportedException => $"yt-dlp could not be prepared: {ex.Message}",
        _ when YtDlpFailure.IsConnection(ex) =>
            "yt-dlp could not be downloaded — no internet connection. YouTube search will not work until it is.",
        _ => "yt-dlp could not be prepared. The log has the reason.",
    };

    private static string DescribeUpdateFailure(Exception ex) => ex switch
    {
        YtDlpTimeoutException =>
            "yt-dlp could not check for updates — it took too long; the installed copy is still used.",
        _ when YtDlpFailure.IsConnection(ex) =>
            "yt-dlp could not check for updates — no internet connection; the installed copy is still used.",
        _ => "yt-dlp could not check for updates; the installed copy is still used. The log has the reason.",
    };
}
