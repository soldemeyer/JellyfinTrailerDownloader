using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TrailerDownloader.Configuration;

/// <summary>Which engine performs the actual download.</summary>
public enum DownloadBackend
{
    /// <summary>yt-dlp binary managed by the plugin, run on the Jellyfin server itself.</summary>
    EmbeddedYtDlp = 0,

    /// <summary>A remote YoutubeDL-Material server reached over its REST API.</summary>
    YoutubeDlMaterial = 1
}

/// <summary>Maximum resolution to request for trailers.</summary>
public enum TrailerQuality
{
    Best = 0,
    Q2160p = 1,
    Q1080p = 2,
    Q720p = 3,
    Q480p = 4
}

/// <summary>Where the trailer file is placed relative to the movie.</summary>
public enum TrailerFileLayout
{
    /// <summary>&lt;MovieFileName&gt;-trailer.ext next to the movie file.</summary>
    Suffix = 0,

    /// <summary>A "trailers" subfolder inside the movie's folder.</summary>
    TrailersFolder = 1
}

/// <summary>How the automatic library-wide download is scheduled.</summary>
public enum ScheduleMode
{
    Disabled = 0,
    Daily = 1,
    Weekly = 2,
    EveryXHours = 3
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets how the automatic download task is scheduled.</summary>
    public ScheduleMode ScheduleMode { get; set; } = ScheduleMode.Weekly;

    /// <summary>Gets or sets the time of day ("HH:mm") for daily/weekly schedules.</summary>
    public string ScheduleTime { get; set; } = "03:00";

    /// <summary>Gets or sets the day of week for weekly schedules.</summary>
    public DayOfWeek ScheduleDayOfWeek { get; set; } = DayOfWeek.Sunday;

    /// <summary>Gets or sets the interval in hours for EveryXHours schedules.</summary>
    public int ScheduleIntervalHours { get; set; } = 24;

    /// <summary>Gets or sets the download engine to use.</summary>
    public DownloadBackend Backend { get; set; } = DownloadBackend.EmbeddedYtDlp;

    /// <summary>Gets or sets the maximum trailer resolution to download.</summary>
    public TrailerQuality Quality { get; set; } = TrailerQuality.Q1080p;

    /// <summary>Gets or sets how trailer files are named/placed.</summary>
    public TrailerFileLayout FileLayout { get; set; } = TrailerFileLayout.Suffix;

    /// <summary>
    /// Gets or sets a value indicating whether to search YouTube for a trailer when the
    /// movie's metadata has no remote trailer URL.
    /// </summary>
    public bool EnableSearchFallback { get; set; } = true;

    /// <summary>Gets or sets the YouTube search template used by the fallback. Supports {title} and {year}.</summary>
    public string SearchTemplate { get; set; } = "{title} {year} official trailer";

    /// <summary>Gets or sets a value indicating whether existing local trailers should be replaced.</summary>
    public bool OverwriteExisting { get; set; } = false;

    /// <summary>Gets or sets a custom path to a yt-dlp binary. Empty = plugin downloads/manages its own copy.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the managed yt-dlp binary is auto-updated weekly.</summary>
    public bool AutoUpdateYtDlp { get; set; } = true;

    /// <summary>Gets or sets extra command line arguments passed to yt-dlp.</summary>
    public string YtDlpExtraArgs { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to a Netscape-format YouTube cookies.txt file, used to
    /// download age-restricted trailers. Empty = no cookies.
    /// </summary>
    public string CookiesFilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL of the YoutubeDL-Material server (e.g. http://192.168.1.10:8998).</summary>
    public string YtdlMaterialUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the YoutubeDL-Material API key.</summary>
    public string YtdlMaterialApiKey { get; set; } = string.Empty;
}
