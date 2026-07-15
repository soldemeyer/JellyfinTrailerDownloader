using Jellyfin.Plugin.TrailerDownloader.Configuration;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>A single trailer download job.</summary>
/// <param name="Url">Video URL, or a yt-dlp "ytsearch1:..." expression.</param>
/// <param name="TargetFolder">Folder the trailer file must end up in.</param>
/// <param name="BaseFileName">Final file name without extension (e.g. "Movie (2020)-trailer").</param>
public record TrailerDownloadRequest(string Url, string TargetFolder, string BaseFileName);

public record TrailerDownloadResult(bool Success, string Message);

public interface ITrailerDownloadBackend
{
    Task<TrailerDownloadResult> DownloadAsync(TrailerDownloadRequest request, PluginConfiguration config, CancellationToken cancellationToken);
}
