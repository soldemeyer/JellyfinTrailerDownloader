using System.IO.Compression;
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

    /// <summary>
    /// Ensures a Deno binary is available for yt-dlp's YouTube JS challenge solving
    /// (see https://github.com/yt-dlp/yt-dlp/wiki/EJS). Returns null on failure so
    /// callers can proceed without a JS runtime.
    /// </summary>
    public async Task<string?> EnsureDenoAsync(CancellationToken cancellationToken)
    {
        var denoPath = Path.Combine(DataFolder, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "deno.exe" : "deno");

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(denoPath))
            {
                return denoPath;
            }

            Directory.CreateDirectory(DataFolder);
            var url = $"https://github.com/denoland/deno/releases/latest/download/{DenoAssetName}.zip";
            _logger.LogInformation("Downloading Deno JS runtime from {Url}", url);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            var zipPath = denoPath + ".zip.tmp";
            await using (var response = await client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
            await using (var file = File.Create(zipPath))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.Entries.First(e =>
                    string.Equals(Path.GetFileNameWithoutExtension(e.Name), "deno", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(denoPath, overwrite: true);
            }
            finally
            {
                File.Delete(zipPath);
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(
                    denoPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            _logger.LogInformation("Deno installed at {Path}", denoPath);
            return denoPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install Deno; YouTube downloads may fail with 403 errors for some videos");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string DenoAssetName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "deno-x86_64-pc-windows-msvc";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "deno-aarch64-apple-darwin"
                    : "deno-x86_64-apple-darwin";
            }

            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "deno-aarch64-unknown-linux-gnu"
                : "deno-x86_64-unknown-linux-gnu";
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
