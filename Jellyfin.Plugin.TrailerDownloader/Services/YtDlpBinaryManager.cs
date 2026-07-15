using System.Runtime.InteropServices;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>
/// Downloads and maintains a self-contained yt-dlp binary inside the plugin data folder,
/// so no external youtube-dl installation is required on the Jellyfin server.
/// </summary>
public class YtDlpBinaryManager
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(7);

    private readonly IApplicationPaths _applicationPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlpBinaryManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public YtDlpBinaryManager(
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<YtDlpBinaryManager> logger)
    {
        _applicationPaths = applicationPaths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string DataFolder => Path.Combine(_applicationPaths.DataPath, "trailerdownloader");

    public string ManagedBinaryPath => Path.Combine(DataFolder, BinaryFileName);

    private static string BinaryFileName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "yt-dlp.exe";
            }

            return "yt-dlp";
        }
    }

    private static string DownloadAssetName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "yt-dlp.exe";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "yt-dlp_macos";
            }

            // Standalone Linux build: does not require Python on the host/container.
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "yt-dlp_linux_aarch64"
                : "yt-dlp_linux";
        }
    }

    /// <summary>
    /// Ensures a usable yt-dlp binary exists and is reasonably fresh, returning its path.
    /// If <paramref name="customPath"/> is set it is used verbatim and never updated.
    /// </summary>
    public async Task<string> EnsureBinaryAsync(string customPath, bool autoUpdate, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (!File.Exists(customPath))
            {
                throw new FileNotFoundException($"Configured yt-dlp path does not exist: {customPath}");
            }

            return customPath;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ManagedBinaryPath;
            var exists = File.Exists(path);
            var stale = exists && autoUpdate && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > UpdateInterval;

            if (!exists || stale)
            {
                try
                {
                    await DownloadBinaryAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (exists)
                {
                    // Keep using the old binary if the update fetch fails.
                    _logger.LogWarning(ex, "yt-dlp update failed, keeping existing binary at {Path}", path);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
            }

            return path;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DownloadBinaryAsync(string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataFolder);
        var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{DownloadAssetName}";
        _logger.LogInformation("Downloading yt-dlp from {Url}", url);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var tempPath = targetPath + ".tmp";
        await using (var response = await client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(tempPath))
        {
            await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, targetPath, overwrite: true);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(
                targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        _logger.LogInformation("yt-dlp binary installed at {Path}", targetPath);
    }
}
