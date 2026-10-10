namespace KHost.Plugins.YouTube.Tests;

public class Mp4TagsTests
{
    [Fact]
    public void Arguments_CopyEveryStreamAndWriteTitleAndArtist()
    {
        var arguments = Mp4Tags.Arguments("in.mp4", "out.mp4", "Africa", "Toto");

        Assert.Equal(
            ["-y", "-v", "error", "-i", "in.mp4", "-map", "0", "-c", "copy",
             "-metadata", "title=Africa", "-metadata", "artist=Toto", "-f", "mp4", "out.mp4"],
            arguments);
    }

    /// <summary>No artist tag at all rather than an empty one, so a re-import falls back to the
    /// file name instead of reading a blank artist.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Arguments_WithNoArtist_WriteNoArtistTag(string? artist)
    {
        var arguments = Mp4Tags.Arguments("in.mp4", "out.mp4", "Africa", artist);

        Assert.DoesNotContain(arguments, a => a.StartsWith("artist=", StringComparison.Ordinal));
        Assert.Contains("title=Africa", arguments);
    }

    /// <summary>A cancelled download's clean-up deletes every "{videoId}.*" beside it; the partial
    /// tag file must be one of them.</summary>
    [Fact]
    public void TaggingPathFor_StartsWithTheVideoIdBesideTheDownload()
    {
        var path = Path.Combine("media", "youtube", "abc123.mp4");

        var tagging = Mp4Tags.TaggingPathFor(path);

        Assert.Equal(Path.Combine("media", "youtube"), Path.GetDirectoryName(tagging));
        Assert.StartsWith("abc123.", Path.GetFileName(tagging));
        Assert.NotEqual(path, tagging);
    }
}
