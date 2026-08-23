using KHost.Plugin.YouTube;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KHost.Plugin.YouTube.Tests;

public class YtDlpResolverTests : IDisposable
{
    private const string Payload = "#!/bin/sh\necho fake yt-dlp\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ytdlp-resolver-{Guid.NewGuid():n}");
    private readonly FakeDownloadHandler _handler = new() { Body = Payload };

    private string ToolsDirectory => Path.Combine(_root, "tools");

    // Empty rather than the real PATH: this machine may or may not have yt-dlp installed, and a test
    // that passes only on one of those machines is worse than no test.
    private YtDlpResolver Resolver(string? configuredPath = null, string? pathVariable = "", string? asset = null)
        => new(configuredPath, ToolsDirectory, pathVariable, _handler, asset);

    public YtDlpResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ResolveAsync_PrefersTheConfiguredPath_WithoutDownloading()
    {
        var configured = Path.Combine(_root, "my-own-yt-dlp");
        await File.WriteAllTextAsync(configured, Payload);

        var resolved = await Resolver(configuredPath: configured).ResolveAsync();

        Assert.Equal(configured, resolved);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredPathThatIsNotThere_ThrowsRatherThanDownloading()
    {
        var missing = Path.Combine(_root, "nowhere", "yt-dlp");

        var resolver = Resolver(configuredPath: missing);

        await Assert.ThrowsAsync<FileNotFoundException>(() => resolver.ResolveAsync());

        // The point of the throw: a silent download leaves the host tuning a binary that is not
        // the one being run.
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToOneAlreadyOnPath()
    {
        var binDirectory = Path.Combine(_root, "bin");
        Directory.CreateDirectory(binDirectory);

        var onPath = Path.Combine(binDirectory, YtDlpResolver.ExecutableName);
        await File.WriteAllTextAsync(onPath, Payload);

        var resolved = await Resolver(pathVariable: binDirectory).ResolveAsync();

        Assert.Equal(onPath, resolved);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ResolveAsync_DownloadsWhenThereIsNothingToFind()
    {
        var resolved = await Resolver().ResolveAsync();

        Assert.Equal(Path.Combine(ToolsDirectory, YtDlpResolver.ExecutableName), resolved);
        Assert.Equal(Payload, await File.ReadAllTextAsync(resolved));

        // The asset itself, plus the SHA2-512SUMS fetch that verifies it before it is ever run.
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Contains(_handler.Requests, r => r.Contains(YtDlpResolver.AssetName, StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Contains("SHA2-512SUMS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_ReusesTheCopyItAlreadyFetched()
    {
        await Resolver().ResolveAsync();
        await Resolver().ResolveAsync();

        // 35MB and a checksum fetch would be an expensive way to say "already have it".
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_ADownloadThatDiesMidStream_LeavesNothingBehindToReuse()
    {
        // Mid-stream, not on connect: the staged file has to actually exist and hold bytes for the
        // cleanup to have anything to do. A handler that throws before the first byte tests nothing.
        _handler.FailMidStream = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Resolver().ResolveAsync());

        // File.Exists is perfectly happy with half a binary, so anything left here would be reused
        // forever and never retried.
        Assert.False(File.Exists(Path.Combine(ToolsDirectory, YtDlpResolver.ExecutableName)));
        Assert.Empty(Directory.Exists(ToolsDirectory) ? Directory.GetFiles(ToolsDirectory) : []);
    }

    [Fact]
    public async Task ResolveAsync_ADownloadThatCannotConnect_LeavesNothingBehindToReuse()
    {
        _handler.Throw = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Resolver().ResolveAsync());

        Assert.False(File.Exists(Path.Combine(ToolsDirectory, YtDlpResolver.ExecutableName)));
    }

    [Fact]
    public async Task ResolveAsync_ADownloadThatDoesNotMatchTheChecksum_LeavesNothingBehindToReuse()
    {
        // A wrong-on-purpose SUMS hash stands in for a tampered release or a corrupted transfer:
        // either way the bytes on disk are not the bytes yt-dlp published.
        _handler.CorruptSumsHash = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver().ResolveAsync());
        Assert.Contains(YtDlpResolver.AssetName, ex.Message);

        Assert.False(File.Exists(Path.Combine(ToolsDirectory, YtDlpResolver.ExecutableName)));
        Assert.Empty(Directory.Exists(ToolsDirectory) ? Directory.GetFiles(ToolsDirectory) : []);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheChecksumsFileCannotBeFetched_LeavesNothingBehindToReuse()
    {
        // No SUMS to check against is exactly as unsafe as a wrong one — a check that no-ops when
        // it cannot run is worthless.
        _handler.SumsMissing = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver().ResolveAsync());

        Assert.False(File.Exists(Path.Combine(ToolsDirectory, YtDlpResolver.ExecutableName)));
        Assert.Empty(Directory.Exists(ToolsDirectory) ? Directory.GetFiles(ToolsDirectory) : []);
    }

    [Fact]
    public async Task ResolveAsync_LeavesTheDownloadRunnable()
    {
        var resolved = await Resolver().ResolveAsync();

        Assert.True(File.Exists(resolved));

        // A zip carries no Unix mode, so a downloaded file arrives at 644 and nothing but the
        // resolver puts the bit back.
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(resolved).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task ResolveAsync_AnArchiveAsset_UnpacksAndReturnsTheLauncherInside()
    {
        // 32-bit ARM Linux is the one target published as a zip, and as a folder bundle rather than
        // a lone binary: the launcher will not start without _internal beside it.
        _handler.Body = null;
        _handler.Asset = "yt-dlp_linux_armv7l.zip";
        _handler.ZipEntries = new()
        {
            ["yt-dlp_linux_armv7l"] = "#!launcher",
            ["_internal/base_library.zip"] = "payload",
        };

        var resolved = await Resolver(asset: "yt-dlp_linux_armv7l.zip").ResolveAsync();

        var expected = Path.Combine(ToolsDirectory, "yt-dlp_linux_armv7l", "yt-dlp_linux_armv7l");

        Assert.Equal(expected, resolved);
        Assert.Equal("#!launcher", await File.ReadAllTextAsync(resolved));
        Assert.True(File.Exists(Path.Combine(ToolsDirectory, "yt-dlp_linux_armv7l", "_internal", "base_library.zip")));

        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(resolved).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task ResolveAsync_AnArchiveItAlreadyUnpacked_IsNotFetchedAgain()
    {
        _handler.Body = null;
        _handler.Asset = "yt-dlp_linux_armv7l.zip";
        _handler.ZipEntries = new() { ["yt-dlp_linux_armv7l"] = "#!launcher" };

        await Resolver(asset: "yt-dlp_linux_armv7l.zip").ResolveAsync();
        await Resolver(asset: "yt-dlp_linux_armv7l.zip").ResolveAsync();

        // The asset plus its SUMS fetch, once — the second resolve finds the unpacked launcher already there.
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ResolveAsync_ADownloadedArchive_DoesNotSurviveAsAStrayFile()
    {
        _handler.Body = null;
        _handler.Asset = "yt-dlp_linux_armv7l.zip";
        _handler.ZipEntries = new() { ["yt-dlp_linux_armv7l"] = "#!launcher" };

        await Resolver(asset: "yt-dlp_linux_armv7l.zip").ResolveAsync();

        // The zip itself is scratch. Leaving 36MB of it behind for every host is not a rounding error.
        Assert.Empty(Directory.GetFiles(ToolsDirectory));
    }

    [Fact]
    public async Task OwnsCopyAt_IsTrueOnlyForTheCopyItFetched()
    {
        var downloaded = await Resolver().ResolveAsync();
        var resolver = Resolver();

        Assert.True(resolver.OwnsCopyAt(downloaded));

        // A packaged install reports itself as a pip build and refuses -U, so the plugin must not
        // try to update one it did not put there.
        Assert.False(resolver.OwnsCopyAt("/opt/homebrew/bin/yt-dlp"));
        Assert.False(resolver.OwnsCopyAt("/usr/local/bin/yt-dlp"));
    }

    [Fact]
    public void AssetName_NamesTheBuildPublishedForThisPlatform()
    {
        var asset = YtDlpResolver.AssetName;

        if (OperatingSystem.IsMacOS())
            // One universal2 build covers both arches, so there is no arm64 variant to pick.
            Assert.Equal("yt-dlp_macos", asset);
        else if (OperatingSystem.IsWindows())
            Assert.EndsWith(".exe", asset);
        else
            // musllinux on Alpine, plain linux elsewhere — the glibc builds will not load on musl.
            Assert.True(
                asset.StartsWith("yt-dlp_linux", StringComparison.Ordinal)
                || asset.StartsWith("yt-dlp_musllinux", StringComparison.Ordinal),
                $"unexpected Linux asset '{asset}'");
    }

    private sealed class FakeDownloadHandler : HttpMessageHandler
    {
        public string? Body { get; set; } = "";
        public bool Throw { get; set; }
        public bool FailMidStream { get; set; }
        public Dictionary<string, string>? ZipEntries { get; set; }
        public List<string> Requests { get; } = [];

        /// <summary>Filename the fake SHA2-512SUMS line credits — must match the asset under test.</summary>
        public string Asset { get; set; } = YtDlpResolver.AssetName;

        /// <summary>Wrong on purpose, to prove a download that doesn't match SUMS is rejected, not trusted.</summary>
        public bool CorruptSumsHash { get; set; }

        /// <summary>The SUMS endpoint 404s, as if the release published none.</summary>
        public bool SumsMissing { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);

            if (Throw) throw new HttpRequestException("network is down");

            if (request.RequestUri!.AbsoluteUri.Contains("SHA2-512SUMS", StringComparison.Ordinal))
                return Task.FromResult(BuildSumsResponse());

            HttpContent content = FailMidStream ? new StreamContent(new TruncatingStream())
                : ZipEntries is not null ? new ByteArrayContent(BuildZip(ZipEntries))
                : new StringContent(Body ?? "");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private HttpResponseMessage BuildSumsResponse()
        {
            if (SumsMissing) return new HttpResponseMessage(HttpStatusCode.NotFound);

            // The real bytes the asset request below will serve — the SUMS line has to match them,
            // or every test would be exercising the mismatch path instead of the happy one.
            var bytes = ZipEntries is not null ? BuildZip(ZipEntries) : Encoding.UTF8.GetBytes(Body ?? "");

            var hash = CorruptSumsHash
                ? new string('0', 128)
                : Convert.ToHexStringLower(SHA512.HashData(bytes));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{hash}  {Asset}\n")
            };
        }

        private static byte[] BuildZip(Dictionary<string, string> entries)
        {
            using var buffer = new MemoryStream();

            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, body) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(body);
                }
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Hands over a few bytes, then drops — a download interrupted, not one refused.</summary>
    private sealed class TruncatingStream : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served) throw new IOException("connection reset mid-download");

            _served = true;

            var chunk = Math.Min(buffer.Length, 8);
            buffer.Span[..chunk].Fill((byte)'x');

            return ValueTask.FromResult(chunk);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
