using System.Net.Mime;
using Jellyfin.Plugin.TrailerDownloader.ScheduledTasks;
using Jellyfin.Plugin.TrailerDownloader.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Api;

/// <summary>REST endpoints backing the plugin configuration page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("TrailerDownloader")]
[Produces(MediaTypeNames.Application.Json)]
public class TrailerDownloaderController : ControllerBase
{
    private readonly TrailerScanner _scanner;
    private readonly TrailerDownloadService _downloadService;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<TrailerDownloaderController> _logger;

    public TrailerDownloaderController(
        TrailerScanner scanner,
        TrailerDownloadService downloadService,
        ITaskManager taskManager,
        ILogger<TrailerDownloaderController> logger)
    {
        _scanner = scanner;
        _downloadService = downloadService;
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

    /// <summary>Downloads the trailer for a single movie immediately.</summary>
    [HttpPost("Download/{itemId}")]
    public async Task<ActionResult<TrailerDownloadResult>> DownloadOne([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var movie = _scanner.GetMovie(itemId);
        if (movie is null)
        {
            return NotFound();
        }

        _logger.LogInformation("Manual trailer download requested for {Movie}", movie.Name);
        var result = await _downloadService.DownloadForMovieAsync(movie, cancellationToken).ConfigureAwait(false);
        return Ok(result);
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
