using Jellyfin.Plugin.TrailerDownloader.Configuration;
using Jellyfin.Plugin.TrailerDownloader.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>
/// Keeps the "Download missing movie trailers" scheduled-task trigger in sync with the
/// schedule configured on the plugin settings page. Runs at server start and after every
/// configuration save.
/// </summary>
public class ScheduleSyncService : IHostedService
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ScheduleSyncService> _logger;

    public ScheduleSyncService(ITaskManager taskManager, ILogger<ScheduleSyncService> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ApplySchedule();

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        ApplySchedule();
    }

    private void ApplySchedule()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var worker = _taskManager.ScheduledTasks
                .FirstOrDefault(t => t.ScheduledTask.Key == DownloadTrailersTask.TaskKey);
            if (worker is null)
            {
                _logger.LogWarning("Trailer download task not found; cannot apply schedule");
                return;
            }

            worker.Triggers = BuildTriggers(config);
            worker.ReloadTriggerEvents();
            _logger.LogInformation("Trailer download schedule applied: {Mode}", config.ScheduleMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply trailer download schedule");
        }
    }

    internal static TaskTriggerInfo[] BuildTriggers(PluginConfiguration config)
    {
        if (!TimeSpan.TryParse(config.ScheduleTime, out var timeOfDay))
        {
            timeOfDay = TimeSpan.FromHours(3);
        }

        return config.ScheduleMode switch
        {
            Configuration.ScheduleMode.Disabled => Array.Empty<TaskTriggerInfo>(),
            Configuration.ScheduleMode.Daily => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = timeOfDay.Ticks
                }
            },
            Configuration.ScheduleMode.EveryXHours => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(Math.Max(1, config.ScheduleIntervalHours)).Ticks
                }
            },
            _ => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.WeeklyTrigger,
                    DayOfWeek = config.ScheduleDayOfWeek,
                    TimeOfDayTicks = timeOfDay.Ticks
                }
            }
        };
    }
}
