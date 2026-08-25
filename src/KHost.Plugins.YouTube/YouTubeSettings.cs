namespace KHost.Plugins.YouTube;

/// <summary>Typed view of the settings declared in manifest.json — keep the two in sync.</summary>
public class YouTubeSettings
{
    /// <summary>
    /// Where yt-dlp lives, when the host wants to say so. Empty means look on PATH and fetch a copy
    /// if there is none — no setup, and no API key anywhere.
    /// </summary>
    public string YtDlpPath { get; set; } = "";

    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Keeps the copy this plugin downloaded current, once per run and in the background. Has no
    /// effect on a yt-dlp installed by brew, apt or winget — that one refuses <c>-U</c> and is its
    /// package manager's to update.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;
}
