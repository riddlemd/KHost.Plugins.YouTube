using System.Net.Sockets;
using KHost.Abstractions.Exceptions;

namespace KHost.Plugins.YouTube.Tests;

public class YtDlpFailureTests
{
    [Theory]
    [InlineData("Failed to establish a new connection")]
    [InlineData("Unable to download API page")]
    [InlineData("getaddrinfo failed")]
    [InlineData("Name or service not known")]
    [InlineData("nodename nor servname provided, or not known")]
    public void IsConnection_YtDlpStderrNamingTheNetwork_IsTrue(string stderr)
        => Assert.True(YtDlpFailure.IsConnection(new InvalidOperationException($"yt-dlp exited with 1: ERROR: {stderr}")));

    [Fact]
    public void IsConnection_IsCaseInsensitive()
        => Assert.True(YtDlpFailure.IsConnection(new InvalidOperationException("GETADDRINFO failed")));

    [Fact]
    public void IsConnection_AnotherYtDlpError_IsFalse()
        => Assert.False(YtDlpFailure.IsConnection(new InvalidOperationException("yt-dlp exited with 1: ERROR: Video unavailable")));

    [Fact]
    public void IsConnection_HttpRequestException_IsTrue()
        => Assert.True(YtDlpFailure.IsConnection(new HttpRequestException("down")));

    [Fact]
    public void IsConnection_SocketException_IsTrue()
        => Assert.True(YtDlpFailure.IsConnection(new SocketException()));

    [Fact]
    public void IsConnection_AStalledFetch_IsTrue()
        => Assert.True(YtDlpFailure.IsConnection(new TimeoutException("no progress")));

    [Fact]
    public void IsConnection_TheCauseIsBuriedInTheInnerExceptions_IsTrue()
        => Assert.True(YtDlpFailure.IsConnection(
            new InvalidOperationException("Could not fetch SUMS", new HttpRequestException("down"))));

    [Fact]
    public void IsConnection_YtDlpRanTooLong_IsFalseEvenThoughItIsATimeout()
        => Assert.False(YtDlpFailure.IsConnection(new YtDlpTimeoutException(TimeSpan.FromSeconds(5))));

    [Fact]
    public void Describe_NotInstalled_SaysItCouldNotBeDownloaded()
        => Assert.Equal(
            "yt-dlp is not installed and could not be downloaded. Check this computer is online, then try again.",
            YtDlpFailure.Describe(new YtDlpUnavailableException(new HttpRequestException("down"))));

    [Fact]
    public void Describe_TookTooLong_SaysSo()
        => Assert.Equal(
            "yt-dlp took too long and was stopped. Try again.",
            YtDlpFailure.Describe(new YtDlpTimeoutException(TimeSpan.FromSeconds(5))));

    [Fact]
    public void Describe_OutOfDate_SaysToUpdate()
        => Assert.Equal(
            "yt-dlp on this machine looks too old. Run 'yt-dlp -U' to update it.",
            YtDlpFailure.Describe(new KHostException("m", "r", YtDlpFailure.OutdatedCode)));

    [Fact]
    public void Describe_ConfiguredPathMissing_PointsAtTheSetting()
        => Assert.Equal(
            "yt-dlp was not found at its configured path. Check the yt-dlp Path setting.",
            YtDlpFailure.Describe(new FileNotFoundException("x")));

    [Fact]
    public void Describe_Offline_UsesTheHostsConnectionWording()
        => Assert.Equal(
            "YouTube could not be reached. Check this computer is online, then try again.",
            YtDlpFailure.Describe(new InvalidOperationException("yt-dlp exited with 1: getaddrinfo failed")));

    [Fact]
    public void Describe_Anything_Else_IsAGenericLineWithoutTheMessage()
        => Assert.Equal("yt-dlp could not fetch it.", YtDlpFailure.Describe(new InvalidOperationException("secret stderr")));

    [Fact]
    public void Detail_TakesTheLastErrorLineWithoutItsPrefix()
        => Assert.Equal(
            "Video unavailable",
            YtDlpFailure.Detail(new InvalidOperationException("yt-dlp exited with 1: ERROR: old\nERROR: Video unavailable\ntrailing")));

    [Fact]
    public void Detail_NoErrorLine_FallsBackToTheMessage()
        => Assert.Equal("boom", YtDlpFailure.Detail(new InvalidOperationException("boom")));

    [Fact]
    public void Detail_AnOverlongLine_IsTrimmed()
    {
        var detail = YtDlpFailure.Detail(new InvalidOperationException("ERROR: " + new string('x', 400)))!;

        Assert.Equal(151, detail.Length);
        Assert.EndsWith("…", detail);
    }

    [Fact]
    public void Detail_ABlankMessage_IsNull()
        => Assert.Null(YtDlpFailure.Detail(new InvalidOperationException("ERROR:   ")));

    [Fact]
    public void Detail_Offline_IsNullBecauseTheWordingAlreadySaysIt()
        => Assert.Null(YtDlpFailure.Detail(new InvalidOperationException("ERROR: getaddrinfo failed")));

    [Fact]
    public void Detail_NotAYtDlpExit_IsNull()
        => Assert.Null(YtDlpFailure.Detail(new FileNotFoundException("path")));
}
