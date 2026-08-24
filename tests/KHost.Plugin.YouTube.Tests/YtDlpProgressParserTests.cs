namespace KHost.Plugin.YouTube.Tests;

public class YtDlpProgressParserTests
{
    [Theory]
    [InlineData("[download]  42.3% of  114.51MiB at    2.91MiB/s ETA 00:25", 0.2115)]
    [InlineData("[download]   0.0% of  114.51MiB at  Unknown B/s ETA Unknown", 0.0)]
    [InlineData("[download] 100% of 114.51MiB in 00:40", 0.5)]
    public void Parse_SingleStreamSoFar_ScalesIntoTheLowerHalf(string line, double expected)
    {
        var (_, fraction) = YtDlpProgressParser.Parse(destinationsSeen: 1, line);

        Assert.Equal(expected, fraction);
    }

    [Fact]
    public void Parse_SoloDownload_NeverSeesASecondDestination_CapsAtTheHalfwayPointRatherThanGoingBackwards()
    {
        // The /b fallback (single format, no merge) logs one Destination line and one 0->100 pass.
        // The parser alone cannot tell in advance that no second stream is coming, so it stays
        // conservative and never exceeds 0.5 here — DownloadAndEnqueueAsync reports the real 1.0
        // itself once the file is confirmed on disk.
        var (destinationsSeen, _) = YtDlpProgressParser.Parse(0, "[download] Destination: video.mp4");
        (_, var fraction) = YtDlpProgressParser.Parse(destinationsSeen, "[download] 100% of 20.00MiB in 00:08");

        Assert.Equal(1, destinationsSeen);
        Assert.Equal(0.5, fraction);
    }

    [Theory]
    [InlineData("[download]   0.0% of   5.00MiB at  Unknown B/s ETA Unknown", 0.5)]
    [InlineData("[download]  50.0% of   5.00MiB at    1.00MiB/s ETA 00:02", 0.75)]
    [InlineData("[download] 100% of 5.00MiB in 00:05", 1.0)]
    public void Parse_SecondStreamSeen_ScalesIntoTheUpperHalf(string line, double expected)
    {
        var (_, fraction) = YtDlpProgressParser.Parse(destinationsSeen: 2, line);

        Assert.Equal(expected, fraction);
    }

    [Fact]
    public void Parse_TwoStreamSequence_TheBoundaryIsContinuousNotBackwards()
    {
        // The video stream (destination 1) finishing at 100% and the audio stream (destination 2)
        // immediately starting at 0% must land on the same fraction — that is the whole point of
        // the 0.5 split.
        var (afterDestination1, _) = YtDlpProgressParser.Parse(0, "[download] Destination: video.f137.mp4");
        var (_, videoEnd) = YtDlpProgressParser.Parse(afterDestination1, "[download] 100% of 50.00MiB in 00:10");
        var (afterDestination2, _) = YtDlpProgressParser.Parse(afterDestination1, "[download] Destination: audio.f140.m4a");
        var (_, audioStart) = YtDlpProgressParser.Parse(afterDestination2, "[download]   0.0% of   5.00MiB at Unknown B/s ETA Unknown");

        Assert.Equal(1, afterDestination1);
        Assert.Equal(2, afterDestination2);
        Assert.Equal(0.5, videoEnd);
        Assert.Equal(0.5, audioStart);
    }

    [Fact]
    public void Parse_DestinationLine_IncrementsCountAndReportsNoFraction()
    {
        var (destinationsSeen, fraction) = YtDlpProgressParser.Parse(0, "[download] Destination: /tmp/video.f137.mp4");

        Assert.Equal(1, destinationsSeen);
        Assert.Null(fraction);
    }

    [Theory]
    [InlineData("[youtube] abc123: Downloading webpage")]
    [InlineData("[Merger] Merging formats into \"abc123.mp4\"")]
    [InlineData("[info] abc123: Downloading 1 format(s): 137+140")]
    [InlineData("")]
    public void Parse_NonDownloadLine_ReportsNoFractionAndLeavesCountUnchanged(string line)
    {
        var (destinationsSeen, fraction) = YtDlpProgressParser.Parse(1, line);

        Assert.Equal(1, destinationsSeen);
        Assert.Null(fraction);
    }

    [Theory]
    [InlineData("[download] % of 5.00MiB")]
    [InlineData("[download] N/A% of Unknown")]
    [InlineData("[download]")]
    public void Parse_MalformedDownloadLine_ReportsNoFraction(string line)
    {
        var (_, fraction) = YtDlpProgressParser.Parse(1, line);

        Assert.Null(fraction);
    }

    [Fact]
    public void Parse_PercentLineBeforeAnyDestination_TreatedAsFirstStream()
    {
        // The /b fallback still logs a Destination line before its own percent lines in practice,
        // but a stray percent line arriving first must not throw or scale into the wrong half.
        var (destinationsSeen, fraction) = YtDlpProgressParser.Parse(0, "[download]  10.0% of 5.00MiB");

        Assert.Equal(0, destinationsSeen);
        Assert.Equal(0.05, fraction);
    }
}
