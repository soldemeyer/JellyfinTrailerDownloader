using System.Net.Mime;
using Jellyfin.Plugin.TrailerDownloader.ScheduledTasks;
using Jellyfin.Plugin.TrailerDownloader.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Api;

/// <summary>Optional body for POST Download/{itemId}.</summary>
public class DownloadRequestDto
{
    /// <summary>Gets or sets a custom video URL to download instead of running discovery.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets a value indicating whether the file is added alongside existing trailers.</summary>
    public bool Additional { get; set; }
}

public record TrailerFileDto(string FileName, long SizeBytes, bool PluginCreated);

public record QueuedDto(bool Queued, string Movie);

public record LibraryStatsDto(int TotalMovies, int WithTrailer, int Missing);

/// <summary>REST endpoints backing the plugin configuration page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TrailerDownloader")]
[Produces(MediaTypeNames.Application.Json)]
public class TrailerDownloaderController : ControllerBase
{
    private readonly TrailerScanner _scanner;
    private readonly TrailerDownloadService _downloadService;
    private readonly TrailerLedger _ledger;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<TrailerDownloaderController> _logger;

    public TrailerDownloaderController(
        TrailerScanner scanner,
        TrailerDownloadService downloadService,
        TrailerLedger ledger,
        ITaskManager taskManager,
        ILogger<TrailerDownloaderController> logger)
    {
        _scanner = scanner;
        _downloadService = downloadService;
        _ledger = ledger;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>Lists all movies with their trailer status.</summary>
    [HttpGet("Movies")]
    public ActionResult<IEnumerable<MovieTrailerInfo>> GetMovies()
    {
        var config = Plugin.Instance!.Configuration;
        var movies = _scanner.GetMovies().Select(m => _scanner.GetInfo(m, config)).ToList();
        return Ok(movies);
    }

    /// <summary>
    /// Downloads a trailer for a single movie immediately. The optional body supplies a
    /// custom video URL and/or requests an additional (indexed) trailer file.
    /// </summary>
    [HttpPost("Download/{itemId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult DownloadOne(
        [FromRoute] Guid itemId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DownloadRequestDto? body)
    {
        var movie = _scanner.GetMovie(itemId);
        if (movie is null)
        {
            return NotFound();
        }

        var customUrl = string.IsNullOrWhiteSpace(body?.Url) ? null : body!.Url.Trim();
        _logger.LogInformation(
            "Manual trailer download queued for {Movie} (customUrl={HasUrl}, additional={Additional})",
            movie.Name,
            customUrl is not null,
            body?.Additional ?? false);

        // Runs in the background: AI search + download can take several minutes, far
        // beyond the web client's request timeout. Completion is reported via /Status.
        _downloadService.QueueDownload(movie, customUrl, body?.Additional ?? false);
        return Accepted(new QueuedDto(true, movie.Name));
    }

    /// <summary>Lists the trailer files that exist for a movie.</summary>
    [HttpGet("Movies/{itemId}/Trailers")]
    public ActionResult<IEnumerable<TrailerFileDto>> GetMovieTrailers([FromRoute] Guid itemId)
    {
        var movie = _scanner.GetMovie(itemId);
        if (movie is null)
        {
            return NotFound();
        }

        var files = _scanner.GetTrailerFilePaths(movie).Select(path =>
        {
            var info = new FileInfo(path);
            return new TrailerFileDto(
                info.Name,
                info.Exists ? info.Length : 0,
                _ledger.IsPluginCreated(path));
        });

        return Ok(files);
    }

    /// <summary>Deletes a single trailer file belonging to a movie.</summary>
    [HttpDelete("Movies/{itemId}/Trailers")]
    public ActionResult DeleteMovieTrailer([FromRoute] Guid itemId, [FromQuery] string fileName)
    {
        var movie = _scanner.GetMovie(itemId);
        if (movie is null)
        {
            return NotFound();
        }

        // Only allow deleting files the scanner itself reports as this movie's trailers,
        // matched by name — never a caller-supplied path.
        var target = _scanner.GetTrailerFilePaths(movie)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return NotFound("No such trailer file for this movie");
        }

        System.IO.File.Delete(target);
        _ledger.Remove(target);
        _logger.LogInformation("Deleted trailer {File} for {Movie}", target, movie.Name);
        return NoContent();
    }

    /// <summary>
    /// Deletes trailers in bulk: every trailer in the library, or only plugin-created ones.
    /// </summary>
    [HttpPost("Trailers/Clear")]
    public ActionResult<int> ClearTrailers([FromQuery] bool pluginOnly = true)
    {
        var deleted = _downloadService.ClearTrailers(pluginOnly);
        return Ok(deleted);
    }

    /// <summary>Library trailer coverage statistics.</summary>
    [HttpGet("Stats")]
    public ActionResult<LibraryStatsDto> GetStats()
    {
        var movies = _scanner.GetMovies();
        var withTrailer = movies.Count(m => _scanner.HasLocalTrailer(m));
        return Ok(new LibraryStatsDto(movies.Count, withTrailer, movies.Count - withTrailer));
    }

    /// <summary>Starts the full library scan/download via the scheduled task.</summary>
    [HttpPost("DownloadAll")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DownloadAll()
    {
        var task = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == DownloadTrailersTask.TaskKey);
        if (task is null)
        {
            return NotFound("Scheduled task not registered");
        }

        _taskManager.Execute(task, new TaskOptions());
        return NoContent();
    }

    /// <summary>Returns current download progress and recent results.</summary>
    [HttpGet("Status")]
    public ActionResult<DownloadStatus> GetStatus()
    {
        return Ok(_downloadService.GetStatus());
    }
}
