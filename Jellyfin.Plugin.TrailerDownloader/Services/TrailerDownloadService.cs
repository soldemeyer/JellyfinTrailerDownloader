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

    /// <summary>Human-readable description of the current pipeline stage.</summary>
    public string? Activity { get; set; }

    /// <summary>Number of queued single-movie downloads not yet finished.</summary>
    public int Pending { get; set; }

    public int Processed { get; set; }

    public int Total { get; set; }

    public IReadOnlyList<DownloadLogEntry> Recent { get; set; } = Array.Empty<DownloadLogEntry>();
}

/// <summary>A trailer discovery source, tried in the user-configured order.</summary>
public enum DiscoverySource
{
    Metadata,
    Ai,
    Search
}

/// <summary>
/// Orchestrates trailer downloads: runs the discovery pipeline in the configured priority
/// order, serializes downloads, records plugin-created files in the ledger, tracks progress
/// for the UI and refreshes items after new trailers land.
/// </summary>
public class TrailerDownloadService
{
    private const int MaxLogEntries = 100;

    private readonly TrailerScanner _scanner;
    private readonly YtDlpBackend _ytDlpBackend;
    private readonly YoutubeDlMaterialBackend _ytdlMaterialBackend;
    private readonly AiTrailerFinder _aiFinder;
    private readonly TrailerLedger _ledger;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TrailerDownloadService> _logger;

    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly List<DownloadLogEntry> _log = new();

    private bool _isRunning;
    private string? _currentMovie;
    private string? _activity;
    private int _pending;
    private int _processed;
    private int _total;

    public TrailerDownloadService(
        TrailerScanner scanner,
        YtDlpBackend ytDlpBackend,
        YoutubeDlMaterialBackend ytdlMaterialBackend,
        AiTrailerFinder aiFinder,
        TrailerLedger ledger,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<TrailerDownloadService> logger)
    {
        _scanner = scanner;
        _ytDlpBackend = ytDlpBackend;
        _ytdlMaterialBackend = ytdlMaterialBackend;
        _aiFinder = aiFinder;
        _ledger = ledger;
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
                Activity = _activity,
                Pending = _pending,
                Processed = _processed,
                Total = _total,
                Recent = _log.AsEnumerable().Reverse().ToList()
            };
        }
    }

    /// <summary>Downloads the trailer(s) for one movie via the discovery pipeline.</summary>
    public Task<TrailerDownloadResult> DownloadForMovieAsync(Movie movie, CancellationToken cancellationToken)
        => DownloadForMovieAsync(movie, customUrl: null, additional: false, cancellationToken);

    /// <summary>
    /// Queues a single-movie download to run in the background, detached from the HTTP
    /// request so long AI searches and downloads survive the web client's request timeout.
    /// Completion is reported through the status log.
    /// </summary>
    public void QueueDownload(Movie movie, string? customUrl, bool additional)
    {
        lock (_statusLock)
        {
            _pending++;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadForMovieAsync(movie, customUrl, additional, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queued trailer download failed for {Movie}", movie.Name);
                Log(movie.Name, new TrailerDownloadResult(false, ex.Message));
            }
            finally
            {
                lock (_statusLock)
                {
                    _pending--;
                }
            }
        });
    }

    private void SetActivity(string? activity)
    {
        lock (_statusLock)
        {
            _activity = activity;
        }
    }

    /// <summary>
    /// Downloads a trailer for one movie. With <paramref name="customUrl"/> set, downloads
    /// exactly that video; with <paramref name="additional"/> true, the file gets an indexed
    /// name so it is added alongside existing trailers instead of replacing them.
    /// </summary>
    public async Task<TrailerDownloadResult> DownloadForMovieAsync(Movie movie, string? customUrl, bool additional, CancellationToken cancellationToken)
    {
        var config = Config;

        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_statusLock)
            {
                _currentMovie = movie.Name;
            }

            TrailerDownloadResult result;
            if (customUrl is not null)
            {
                result = await TryDownloadAsync(movie, config, customUrl, additional, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await RunDiscoveryPipelineAsync(movie, config, additional, cancellationToken).ConfigureAwait(false);
            }

            return Log(movie.Name, result);
        }
        finally
        {
            lock (_statusLock)
            {
                _currentMovie = null;
                _activity = null;
            }

            _downloadLock.Release();
        }
    }

    private async Task<TrailerDownloadResult> RunDiscoveryPipelineAsync(Movie movie, PluginConfiguration config, bool additional, CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var source in ParseDiscoveryOrder(config))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (source)
            {
                case DiscoverySource.Metadata:
                {
                    SetActivity("Checking metadata trailer URL…");
                    var url = _scanner.GetRemoteTrailerUrl(movie);
                    if (url is null)
                    {
                        continue;
                    }

                    var result = await TryDownloadAsync(movie, config, url, additional, cancellationToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        return result;
                    }

                    failures.Add($"Metadata: {result.Message}");
                    break;
                }

                case DiscoverySource.Ai:
                {
                    SetActivity($"Asking {config.AiProvider} to find official trailers…");
                    var urls = await _aiFinder.FindTrailerUrlsAsync(movie, config, SetActivity, cancellationToken).ConfigureAwait(false);
                    if (urls.Count == 0)
                    {
                        SetActivity("AI found no trailers");
                        continue;
                    }

                    SetActivity($"AI found {urls.Count} trailer(s)");

                    var successes = new List<string>();
                    var isFirst = true;
                    var index = 0;
                    var toDownload = (config.AiDownloadAllResults ? urls : urls.Take(1)).ToList();
                    foreach (var url in toDownload)
                    {
                        index++;
                        SetActivity($"Downloading trailer {index} of {toDownload.Count}…");
                        // The first file uses the caller's naming mode; extra AI results
                        // always get indexed names so they can coexist.
                        var result = await TryDownloadAsync(movie, config, url, additional || !isFirst, cancellationToken).ConfigureAwait(false);
                        if (result.Success)
                        {
                            successes.Add(result.Message);
                        }
                        else
                        {
                            failures.Add($"AI ({url}): {result.Message}");
                        }

                        if (successes.Count > 0)
                        {
                            isFirst = false;
                        }
                    }

                    if (successes.Count > 0)
                    {
                        return new TrailerDownloadResult(true, $"AI search: downloaded {successes.Count} trailer(s)");
                    }

                    break;
                }

                case DiscoverySource.Search:
                {
                    SetActivity("Searching YouTube…");
                    var expr = _scanner.BuildSearchExpression(movie, config);
                    if (expr is null)
                    {
                        continue;
                    }

                    var result = await TryDownloadAsync(movie, config, expr, additional, cancellationToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        return result with { Message = result.Message + " (via YouTube search)" };
                    }

                    failures.Add($"Search: {result.Message}");
                    break;
                }
            }
        }

        return new TrailerDownloadResult(
            false,
            failures.Count > 0
                ? string.Join(" | ", failures)
                : "No trailer source produced a URL (no metadata trailer, AI disabled or empty, search fallback disabled)");
    }

    private async Task<TrailerDownloadResult> TryDownloadAsync(Movie movie, PluginConfiguration config, string url, bool additional, CancellationToken cancellationToken)
    {
        var request = _scanner.BuildRequest(movie, config, url, additional);
        if (request is null)
        {
            return new TrailerDownloadResult(false, "Movie has no usable folder path");
        }

        var result = await GetBackend(config).DownloadAsync(request, config, SetActivity, cancellationToken).ConfigureAwait(false);
        if (result.Success && config.Backend == DownloadBackend.EmbeddedYtDlp)
        {
            // On success the embedded backend's message is the final file path.
            if (File.Exists(result.Message))
            {
                _ledger.Record(result.Message, movie.Id, url);
            }

            QueueRefresh(movie);
        }

        return result;
    }

    internal static IReadOnlyList<DiscoverySource> ParseDiscoveryOrder(PluginConfiguration config)
    {
        var sources = new List<DiscoverySource>();
        foreach (var token in (config.DiscoveryOrder ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<DiscoverySource>(token, ignoreCase: true, out var source) || sources.Contains(source))
            {
                continue;
            }

            if (source == DiscoverySource.Ai && config.AiProvider == AiProvider.None)
            {
                continue;
            }

            if (source == DiscoverySource.Search && !config.EnableSearchFallback)
            {
                continue;
            }

            sources.Add(source);
        }

        if (sources.Count == 0)
        {
            sources.Add(DiscoverySource.Metadata);
        }

        return sources;
    }

    /// <summary>
    /// Deletes trailer files: all of them, or only ones recorded as plugin-created.
    /// Returns the number of files deleted.
    /// </summary>
    public int ClearTrailers(bool pluginOnly)
    {
        var deleted = 0;

        if (pluginOnly)
        {
            foreach (var entry in _ledger.GetAll().ToList())
            {
                try
                {
                    File.Delete(entry.FilePath);
                    _ledger.Remove(entry.FilePath);
                    deleted++;

                    if (_scanner.GetMovie(entry.MovieId) is { } movie)
                    {
                        QueueRefresh(movie);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete plugin trailer {Path}", entry.FilePath);
                }
            }
        }
        else
        {
            foreach (var movie in _scanner.GetMovies())
            {
                foreach (var path in _scanner.GetTrailerFilePaths(movie))
                {
                    try
                    {
                        File.Delete(path);
                        _ledger.Remove(path);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete trailer {Path}", path);
                    }
                }

                QueueRefresh(movie);
            }
        }

        _logger.LogInformation("Cleared {Count} trailer file(s) (pluginOnly={PluginOnly})", deleted, pluginOnly);
        return deleted;
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
            var movies = _scanner.GetMovies();

            var pending = movies
                .Where(m => !_scanner.HasLocalTrailer(m))
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
            _logger.LogWarning(ex, "Failed to queue refresh for {Movie}; changes will appear after the next library scan", movie.Name);
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
