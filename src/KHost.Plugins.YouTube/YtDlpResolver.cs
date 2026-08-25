using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KHost.Plugins.YouTube;

/// <summary>
/// Finds yt-dlp in the order a host would want it found: the path they configured, then whatever is
/// already on the machine, then a copy this plugin fetches for itself on Windows, macOS or Linux.
/// </summary>
/// <remarks>
/// Deliberately not a packaged binary. yt-dlp publishes nightly and updates itself in place, so
/// pinning a copy inside the plugin would trade a same-day extractor fix for a plugin rebuild and a
/// re-release — which is the failure mode yt-dlp was chosen over.
/// </remarks>
public sealed class YtDlpResolver
{
    private const string LatestAssetUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download";
    private const string SumsUrl = LatestAssetUrl + "/SHA2-512SUMS";

    private readonly string? _configuredPath;
    private readonly string _toolsDirectory;
    private readonly string? _pathVariable;
    private readonly HttpMessageHandler? _handler;
    private readonly string _asset;

    /// <param name="asset">
    /// Overrides the asset for this platform. Only a test has cause to set it: the archive build is
    /// published for one target, and its unpacking is not code to leave unrun until a host on that
    /// target finds out.
    /// </param>
    public YtDlpResolver(
        string? configuredPath,
        string toolsDirectory,
        string? pathVariable = null,
        HttpMessageHandler? handler = null,
        string? asset = null)
    {
        _configuredPath = configuredPath;
        _toolsDirectory = toolsDirectory;
        _pathVariable = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        _handler = handler;
        _asset = asset ?? AssetName;
    }

    /// <summary>The release asset this OS and architecture needs.</summary>
    /// <exception cref="PlatformNotSupportedException">No build is published for this combination.</exception>
    public static string AssetName
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "yt-dlp_arm64.exe",
                    Architecture.X86 => "yt-dlp_x86.exe",
                    _ => "yt-dlp.exe",
                };

            // One universal2 build covers both Intel and Apple silicon, so architecture is moot here.
            if (OperatingSystem.IsMacOS())
                return "yt-dlp_macos";

            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException($"yt-dlp publishes no build for {RuntimeInformation.OSDescription}.");

            // Alpine and friends: the glibc builds will not load against musl's loader.
            if (IsMusl)
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "yt-dlp_musllinux_aarch64",
                    Architecture.X64 => "yt-dlp_musllinux",
                    _ => throw new PlatformNotSupportedException(
                        "yt-dlp publishes no musl build for 32-bit ARM. Install it yourself and set the yt-dlp path setting."),
                };

            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",

                // The one target published only as an archive, and a directory bundle rather than a
                // single file — see Extract.
                Architecture.Arm => "yt-dlp_linux_armv7l.zip",

                _ => "yt-dlp_linux",
            };
        }
    }

    /// <summary>What the binary is called once it is ours, whatever the asset was named.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    /// <summary>
    /// Whether this path is a copy the plugin downloaded, and so one it may update in place.
    /// A yt-dlp installed by brew, apt or winget reports itself as a pip build and refuses
    /// <c>-U</c>: updating that one is its package manager's job, not ours.
    /// </summary>
    public bool OwnsCopyAt(string executablePath)
        => Path.GetFullPath(executablePath)
            .StartsWith(Path.GetFullPath(_toolsDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// True on musl libc, where the ordinary Linux builds will not run. The runtime identifier says
    /// so on a musl .NET build; the loader on disk is the fallback for a portable one.
    /// </summary>
    private static bool IsMusl
        => RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            || (Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any());

    public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_configuredPath))
        {
            // A configured path that is wrong is a mistake to report. Quietly downloading a second
            // copy would leave the host tuning a binary that is not the one being run.
            if (!File.Exists(_configuredPath))
                throw new FileNotFoundException(
                    $"yt-dlp is not at the configured path '{_configuredPath}'.", _configuredPath);

            return _configuredPath;
        }

        return FindOnPath() ?? await DownloadAsync(cancellationToken);
    }

    private string? FindOnPath()
    {
        if (string.IsNullOrEmpty(_pathVariable)) return null;

        foreach (var directory in _pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), ExecutableName);

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private async Task<string> DownloadAsync(CancellationToken cancellationToken)
    {
        var asset = _asset;
        var isArchive = asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // An archive unpacks to a folder of its own: the launcher will not run without the
        // _internal directory that ships beside it.
        var destination = isArchive
            ? Path.Combine(_toolsDirectory, Path.GetFileNameWithoutExtension(asset), Path.GetFileNameWithoutExtension(asset))
            : Path.Combine(_toolsDirectory, ExecutableName);

        if (File.Exists(destination)) return destination;

        Directory.CreateDirectory(_toolsDirectory);

        // Staged, then moved. A download that dies halfway leaves a truncated file that File.Exists
        // is perfectly happy with, and every later run would reuse it without ever retrying.
        var staging = Path.Combine(_toolsDirectory, asset + ".downloading");

        using var http = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);

        try
        {
            await using (var source = await http.GetStreamAsync($"{LatestAssetUrl}/{asset}", cancellationToken))
            await using (var file = File.Create(staging))
            {
                await source.CopyToAsync(file, cancellationToken);
            }

            await VerifyChecksumAsync(http, staging, asset, cancellationToken);

            if (isArchive)
                Extract(staging, destination);
            else
                File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }

        MakeExecutable(destination);

        return destination;
    }

    /// <summary>
    /// A compromised release or a broken TLS chain both look like an ordinary successful download;
    /// the published SUMS file is the only thing that says whether the bytes on disk are the bytes
    /// yt-dlp actually shipped. Every way of failing to confirm that — the SUMS file not fetching,
    /// the asset having no line in it, the hash not matching — is fatal, not a silent pass-through
    /// to executing an unverified binary.
    /// </summary>
    private static async Task VerifyChecksumAsync(
        HttpClient http, string stagedFile, string asset, CancellationToken cancellationToken)
    {
        string sums;

        try
        {
            using var response = await http.GetAsync(SumsUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            sums = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not fetch yt-dlp's SHA2-512SUMS to verify '{asset}' before running it.", ex);
        }

        var expected = FindExpectedHash(sums, asset)
            ?? throw new InvalidOperationException(
                $"yt-dlp's SHA2-512SUMS has no entry for '{asset}' — refusing to run an unverified download.");

        string actual;
        await using (var stream = File.OpenRead(stagedFile))
        {
            actual = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, cancellationToken));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Downloaded '{asset}' does not match yt-dlp's published SHA-512 checksum — refusing to run it.");
    }

    /// <summary>Each line is "&lt;128-hex sha512&gt;  &lt;filename&gt;"; the archive case's filename includes ".zip".</summary>
    private static string? FindExpectedHash(string sums, string asset)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && string.Equals(parts[^1], asset, StringComparison.Ordinal))
                return parts[0];
        }

        return null;
    }

    /// <summary>Unpacks beside the launcher, then swaps the whole folder into place.</summary>
    private static void Extract(string archive, string destination)
    {
        var target = Path.GetDirectoryName(destination)!;
        var staging = target + ".unpacking";

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        try
        {
            ZipFile.ExtractToDirectory(archive, staging);

            // Moved whole rather than file by file: a half-populated folder still has a launcher in
            // it, and File.Exists on that launcher would call the install good.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Nothing sets this for us: a zip carries no Unix mode that extraction is obliged to honour, so
    /// neither a download nor an unpack arrives executable.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
