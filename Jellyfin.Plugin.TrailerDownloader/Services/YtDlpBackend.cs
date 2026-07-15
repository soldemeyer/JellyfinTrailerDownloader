using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.TrailerDownloader.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>
/// Downloads trailers by running the plugin-managed yt-dlp binary directly on the
/// Jellyfin server, writing straight into the movie folder.
/// </summary>
public class YtDlpBackend : ITrailerDownloadBackend
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly YtDlpBinaryManager _binaryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<YtDlpBackend> _logger;

    public YtDlpBackend(YtDlpBinaryManager binaryManager, IMediaEncoder mediaEncoder, ILogger<YtDlpBackend> logger)
    {
        _binaryManager = binaryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public async Task<TrailerDownloadResult> DownloadAsync(TrailerDownloadRequest request, PluginConfiguration config, CancellationToken cancellationToken)
    {
        string binary;
        try
        {
            binary = await _binaryManager.EnsureBinaryAsync(config.YtDlpPath, config.AutoUpdateYtDlp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new TrailerDownloadResult(false, $"yt-dlp unavailable: {ex.Message}");
        }

        Directory.CreateDirectory(request.TargetFolder);

        // Download under a temporary name so a half-finished file is never picked up
        // as a trailer; renamed to the final name only on success.
        var tempBase = request.BaseFileName + ".download";
        var outputTemplate = Path.Combine(request.TargetFolder, tempBase + ".%(ext)s");

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(GetFormatString(config.Quality));
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("--no-mtime");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputTemplate);

        var ffmpegDir = GetFfmpegDirectory();
        if (ffmpegDir is not null)
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegDir);
        }

        // YouTube requires a JS runtime for player challenges; without one many
        // videos fail with HTTP 403 (see yt-dlp EJS wiki).
        var denoPath = await _binaryManager.EnsureDenoAsync(cancellationToken).ConfigureAwait(false);
        if (denoPath is not null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("deno:" + denoPath);
        }

        if (!string.IsNullOrWhiteSpace(config.CookiesFilePath))
        {
            if (File.Exists(config.CookiesFilePath))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(config.CookiesFilePath);
            }
            else
            {
                _logger.LogWarning("Configured cookies file does not exist: {Path}", config.CookiesFilePath);
            }
        }

        foreach (var arg in SplitArgs(config.YtDlpExtraArgs))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(request.Url);

        _logger.LogInformation("Running yt-dlp for {Url} -> {Template}", request.Url, outputTemplate);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DownloadTimeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process may have already exited
            }

            CleanupTempFiles(request.TargetFolder, tempBase);
            var reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timed out";
            return new TrailerDownloadResult(false, $"Download {reason}");
        }

        if (process.ExitCode != 0)
        {
            CleanupTempFiles(request.TargetFolder, tempBase);
            var tail = GetTail(output.ToString(), 800);
            _logger.LogWarning("yt-dlp exited with {Code} for {Url}: {Output}", process.ExitCode, request.Url, tail);
            return new TrailerDownloadResult(false, $"yt-dlp failed (exit {process.ExitCode}): {tail}");
        }

        // Rename the finished download (unknown extension) to its final trailer name.
        var downloaded = Directory.EnumerateFiles(request.TargetFolder, tempBase + ".*")
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (downloaded.Count == 0)
        {
            return new TrailerDownloadResult(false, "yt-dlp reported success but no output file was found");
        }

        var source = downloaded[0];
        var finalPath = Path.Combine(
            request.TargetFolder,
            request.BaseFileName + Path.GetExtension(source));

        File.Move(source, finalPath, overwrite: true);
        CleanupTempFiles(request.TargetFolder, tempBase);

        _logger.LogInformation("Trailer saved to {Path}", finalPath);
        return new TrailerDownloadResult(true, finalPath);
    }

    private string? GetFfmpegDirectory()
    {
        try
        {
            var encoderPath = _mediaEncoder.EncoderPath;
            if (!string.IsNullOrEmpty(encoderPath) && File.Exists(encoderPath))
            {
                return Path.GetDirectoryName(encoderPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve Jellyfin ffmpeg path");
        }

        return null;
    }

    internal static string GetFormatString(TrailerQuality quality)
    {
        return quality switch
        {
            TrailerQuality.Best => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
            TrailerQuality.Q2160p => HeightLimited(2160),
            TrailerQuality.Q1080p => HeightLimited(1080),
            TrailerQuality.Q720p => HeightLimited(720),
            TrailerQuality.Q480p => HeightLimited(480),
            _ => HeightLimited(1080)
        };

        static string HeightLimited(int h) =>
            $"bestvideo[height<={h}][ext=mp4]+bestaudio[ext=m4a]/best[height<={h}][ext=mp4]/best[height<={h}]/best";
    }

    /// <summary>Quote-aware splitter for the user's extra-args string.</summary>
    internal static IEnumerable<string> SplitArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void CleanupTempFiles(string folder, string tempBase)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, tempBase + ".*"))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string GetTail(string text, int maxChars)
    {
        text = text.Trim();
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
