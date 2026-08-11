namespace KHost.Plugin.YouTube;

/// <summary>Typed view of the settings declared in manifest.json — keep the two in sync.</summary>
public class YouTubeSettings
{
    public string ApiKey { get; set; } = "";
    public int MaxResults { get; set; } = 10;
}
