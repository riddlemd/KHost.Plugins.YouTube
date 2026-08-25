using KHost.Plugins.YouTube;

namespace KHost.Plugins.YouTube.Tests;

public class YtDlpOutOfDateTests
{
    // The signatures YouTube's defences actually produce. A host running a stale yt-dlp sees one
    // of these and can fix it in a minute; reported as a bare non-zero exit they cannot.
    [Theory]
    [InlineData("yt-dlp exited with 1: ERROR: unable to download video data: HTTP Error 403: Forbidden")]
    [InlineData("ERROR: [youtube] abc: nsig extraction failed: Some formats may be missing")]
    [InlineData("ERROR: [youtube] abc: Unable to extract player response")]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you're not a bot")]
    [InlineData("ERROR: [youtube] abc: Requested format is not available")]
    public void LooksOutOfDate_SignatureOfYouTubeRefusingAStaleClient_IsRecognised(string message)
    {
        Assert.True(YtDlp.LooksOutOfDate(message));
    }

    // A failure that updating cannot fix must not claim it can, or the advice becomes noise.
    [Theory]
    [InlineData("yt-dlp exited with 1: ERROR: Video unavailable. This video is private")]
    [InlineData("yt-dlp exited with 1: ERROR: unable to resolve host address 'youtube.com'")]
    [InlineData("yt-dlp exited with 2: ERROR: no such option --nonsense")]
    [InlineData("Could not start yt-dlp at 'C:\\nope\\yt-dlp.exe'.")]
    public void LooksOutOfDate_FailureUpdatingWouldNotFix_IsNotRecognised(string message)
    {
        Assert.False(YtDlp.LooksOutOfDate(message));
    }

    // yt-dlp's own casing has moved between releases, so matching has to survive it.
    [Fact]
    public void LooksOutOfDate_SignatureInDifferentCasing_IsStillRecognised()
    {
        Assert.True(YtDlp.LooksOutOfDate("ERROR: UNABLE TO EXTRACT player response"));
    }
}
