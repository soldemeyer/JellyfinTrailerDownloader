using System.Net.Http.Json;
using Jellyfin.Plugin.TrailerDownloader.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>
/// Queues trailer downloads on a remote YoutubeDL-Material server via its public API
/// (POST /api/downloadFile with an apiKey query parameter).
/// Note: files land in the YTDLM server's own download directory; trailers only end up
/// attached to movies if that directory maps onto the Jellyfin library paths.
/// </summary>
public class YoutubeDlMaterialBackend : ITrailerDownloadBackend
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YoutubeDlMaterialBackend> _logger;

    public YoutubeDlMaterialBackend(IHttpClientFactory httpClientFactory, ILogger<YoutubeDlMaterialBackend> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TrailerDownloadResult> DownloadAsync(TrailerDownloadRequest request, PluginConfiguration config, Action<string>? onActivity, CancellationToken cancellationToken)
    {
        onActivity?.Invoke("Queueing on YoutubeDL-Material…");

        if (string.IsNullOrWhiteSpace(config.YtdlMaterialUrl))
        {
            return new TrailerDownloadResult(false, "YoutubeDL-Material server URL is not configured");
        }

        var baseUrl = config.YtdlMaterialUrl.TrimEnd('/');
        var uri = $"{baseUrl}/api/downloadFile?apiKey={Uri.EscapeDataString(config.YtdlMaterialApiKey)}";

        var payload = new
        {
            url = request.Url,
            type = "video",
            maxHeight = MaxHeight(config.Quality),
            customOutput = request.BaseFileName
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var response = await client.PostAsJsonAsync(uri, payload, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("YoutubeDL-Material returned {Status}: {Body}", response.StatusCode, body);
                return new TrailerDownloadResult(false, $"YoutubeDL-Material returned {(int)response.StatusCode}");
            }

            return new TrailerDownloadResult(
                true,
                $"Queued on YoutubeDL-Material as '{request.BaseFileName}' (file stays on the YTDLM server unless it shares library paths)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reach YoutubeDL-Material at {Url}", baseUrl);
            return new TrailerDownloadResult(false, $"Could not reach YoutubeDL-Material: {ex.Message}");
        }
    }

    private static string? MaxHeight(TrailerQuality quality) => quality switch
    {
        TrailerQuality.Q2160p => "2160",
        TrailerQuality.Q1080p => "1080",
        TrailerQuality.Q720p => "720",
        TrailerQuality.Q480p => "480",
        _ => null
    };
}
