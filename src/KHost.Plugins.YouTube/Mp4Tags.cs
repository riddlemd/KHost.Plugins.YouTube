using System.Diagnostics;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;

namespace KHost.Plugins.YouTube;

/// <summary>Writes a title and artist into a downloaded mp4 in place; the seam tests substitute
/// for a real ffmpeg.</summary>
public delegate Task Mp4Tagger(string path, string title, string? artist, CancellationToken cancellationToken);

/// <summary>Tags a download with the library's title and artist, so a later re-import of the
/// folder finds them in the file rather than reading a video id off its name.</summary>
internal static class Mp4Tags
{
    /// <summary>The partial file a tag pass writes beside its source. Starts with the video id, so
    /// a cancelled download's clean-up sweeps it with the rest.</summary>
    internal static string TaggingPathFor(string path)
        => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".tagging.mp4");

    /// <summary>A stream copy: no re-encode, so it takes as long as copying the file.</summary>
    internal static IReadOnlyList<string> Arguments(string source, string output, string title, string? artist)
    {
        List<string> arguments = ["-y", "-v", "error", "-i", source, "-map", "0", "-c", "copy", "-metadata", $"title={title}"];

        if (!string.IsNullOrWhiteSpace(artist))
            arguments.AddRange(["-metadata", $"artist={artist}"]);

        arguments.AddRange(["-f", "mp4", output]);
        return arguments;
    }

    /// <summary>Runs the host's own ffmpeg; the source is replaced only once the tagged copy is whole.</summary>
    internal static Mp4Tagger Using(IFFmpegService ffmpeg) => async (path, title, artist, cancellationToken) =>
    {
        var executable = ffmpeg.Locate(FFmpegTool.FFmpeg)
            ?? throw new FileNotFoundException("No ffmpeg found to tag the download with.");

        var output = TaggingPathFor(path);

        try
        {
            await RunAsync(executable, Arguments(path, output, title, artist), cancellationToken);
            File.Move(output, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    };

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList, never a joined string: a title carrying a quote would split into arguments.
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start ffmpeg at '{executable}'.");

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(standardOutput, standardError);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {standardError.Result.Trim()}");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            throw;
        }
    }
}
