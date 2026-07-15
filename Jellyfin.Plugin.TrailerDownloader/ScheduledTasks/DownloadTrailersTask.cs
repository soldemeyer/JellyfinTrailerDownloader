using Jellyfin.Plugin.TrailerDownloader.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TrailerDownloader.ScheduledTasks;

/// <summary>
/// Scheduled task that scans the whole movie library and downloads missing trailers.
/// Schedule is user-adjustable under Dashboard → Scheduled Tasks (default: weekly).
/// </summary>
public class DownloadTrailersTask : IScheduledTask, IConfigurableScheduledTask
{
    public const string TaskKey = "TrailerDownloaderDownloadAll";

    private readonly TrailerDownloadService _downloadService;

    public DownloadTrailersTask(TrailerDownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    public string Name => "Download missing movie trailers";

    public string Key => TaskKey;

    public string Description => "Scans the movie library and downloads trailers from YouTube for movies without a local trailer.";

    public string Category => "Trailer Downloader";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _downloadService.RunLibraryScanAsync(progress, cancellationToken);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }
}
