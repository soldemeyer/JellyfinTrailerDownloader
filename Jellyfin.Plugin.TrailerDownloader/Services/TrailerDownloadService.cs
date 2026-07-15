using Jellyfin.Plugin.TrailerDownloader.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

public record DownloadLogEntry(DateTime TimeUtc, string Movie, bool Success, string Message);

public class DownloadStatus
{
    public bool IsRunning { get; set; }

    public string? CurrentMovie { get; set; }

    public int Processed { get; set; }

    public int Total { get; set; }

    public IReadOnlyList<DownloadLogEntry> Recent { get; set; } = Array.Empty<DownloadLogEntry>();
}

/// <summary>
/// Orchestrates trailer downloads: picks the configured backend, serializes downloads,
/// tracks progress for the UI and refreshes items after new trailers land.
/// </summary>
public class TrailerDownloadService
{
    private const int MaxLogEntries = 100;

    private readonly TrailerScanner _scanner;
    private readonly YtDlpBackend _ytDlpBackend;
    private readonly YoutubeDlMaterialBackend _ytdlMaterialBackend;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TrailerDownloadService> _logger;

    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly List<DownloadLogEntry> _log = new();

    private bool _isRunning;
    private string? _currentMovie;
    private int _processed;
    private int _total;

    public TrailerDownloadService(
        TrailerScanner scanner,
        YtDlpBackend ytDlpBackend,
        YoutubeDlMaterialBackend ytdlMaterialBackend,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<TrailerDownloadService> logger)
    {
        _scanner = scanner;
        _ytDlpBackend = ytDlpBackend;
        _ytdlMaterialBackend = ytdlMaterialBackend;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private ITrailerDownloadBackend GetBackend(PluginConfiguration config) =>
        config.Backend == DownloadBackend.YoutubeDlMaterial ? _ytdlMaterialBackend : _ytDlpBackend;

    public DownloadStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new DownloadStatus
            {
                IsRunning = _isRunning,
                CurrentMovie = _currentMovie,
                Processed = _processed,
                Total = _total,
                Recent = _log.AsEnumerable().Reverse().ToList()
            };
        }
    }

    /// <summary>Downloads the trailer for one movie (used by the per-movie UI button).</summary>
    public async Task<TrailerDownloadResult> DownloadForMovieAsync(Movie movie, CancellationToken cancellationToken)
    {
        var config = Config;

        if (!config.OverwriteExisting && _scanner.HasLocalTrailer(movie))
        {
            return Log(movie.Name, new TrailerDownloadResult(true, "Skipped: local trailer already exists"));
        }

        var url = _scanner.ResolveDownloadUrl(movie, config);
        if (url is null)
        {
            return Log(movie.Name, new TrailerDownloadResult(false, "No trailer URL in metadata and search fallback is disabled"));
        }

        var request = _scanner.BuildRequest(movie, config, url);
        if (request is null)
        {
            return Log(movie.Name, new TrailerDownloadResult(false, "Movie has no usable folder path"));
        }

        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_statusLock)
            {
                _currentMovie = movie.Name;
            }

            var result = await GetBackend(config).DownloadAsync(request, config, cancellationToken).ConfigureAwait(false);

            // Metadata trailer URLs are often dead (age-gated, copyright-blocked,
            // private or deleted uploads). When the direct URL fails, retry once with
            // a YouTube search — alternative uploads of trailers are nearly always
            // available.
            if (!result.Success
                && !url.StartsWith("ytsearch", StringComparison.OrdinalIgnoreCase)
                && _scanner.BuildSearchExpression(movie, config) is { } searchExpression)
            {
                _logger.LogInformation(
                    "Direct trailer URL failed for {Movie}, retrying via YouTube search: {Search}",
                    movie.Name,
                    searchExpression);

                var firstError = result.Message;
                var retryRequest = request with { Url = searchExpression };
                result = await GetBackend(config).DownloadAsync(retryRequest, config, cancellationToken).ConfigureAwait(false);
                result = result.Success
                    ? result with { Message = result.Message + " (metadata URL failed; used YouTube search instead)" }
                    : result with { Message = $"Metadata URL failed: {firstError} | Search retry failed: {result.Message}" };
            }

            if (result.Success && config.Backend == DownloadBackend.EmbeddedYtDlp)
            {
                QueueRefresh(movie);
            }

            return Log(movie.Name, result);
        }
        finally
        {
            lock (_statusLock)
            {
                _currentMovie = null;
            }

            _downloadLock.Release();
        }
    }

    /// <summary>Full library pass, used by the scheduled task and the "download all" button.</summary>
    public async Task RunLibraryScanAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            if (_isRunning)
            {
                _logger.LogInformation("Trailer library scan already running, skipping duplicate start");
                return;
            }

            _isRunning = true;
            _processed = 0;
            _total = 0;
        }

        try
        {
            var config = Config;
            var movies = _scanner.GetMovies();

            var pending = movies
                .Where(m => config.OverwriteExisting || !_scanner.HasLocalTrailer(m))
                .ToList();

            lock (_statusLock)
            {
                _total = pending.Count;
            }

            _logger.LogInformation("Trailer scan: {Pending} of {Total} movies need trailers", pending.Count, movies.Count);

            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var movie = pending[i];

                try
                {
                    await DownloadForMovieAsync(movie, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trailer download failed for {Movie}", movie.Name);
                    Log(movie.Name, new TrailerDownloadResult(false, ex.Message));
                }

                lock (_statusLock)
                {
                    _processed = i + 1;
                }

                progress?.Report((i + 1) * 100.0 / pending.Count);
            }

            progress?.Report(100);
        }
        finally
        {
            lock (_statusLock)
            {
                _isRunning = false;
                _currentMovie = null;
            }
        }
    }

    private void QueueRefresh(Movie movie)
    {
        try
        {
            _providerManager.QueueRefresh(
                movie.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false
                },
                RefreshPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue refresh for {Movie}; the trailer will appear after the next library scan", movie.Name);
        }
    }

    private TrailerDownloadResult Log(string movie, TrailerDownloadResult result)
    {
        lock (_statusLock)
        {
            _log.Add(new DownloadLogEntry(DateTime.UtcNow, movie, result.Success, result.Message));
            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveRange(0, _log.Count - MaxLogEntries);
            }
        }

        return result;
    }
}
