using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

public class LedgerEntry
{
    public string FilePath { get; set; } = string.Empty;

    public Guid MovieId { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public DateTime DownloadedAtUtc { get; set; }
}

/// <summary>
/// Persistent record of trailer files downloaded by this plugin, so plugin-created
/// trailers can be identified and cleaned up independently of pre-existing ones.
/// Stored as JSON in the plugin data folder.
/// </summary>
public class TrailerLedger
{
    private readonly string _ledgerPath;
    private readonly ILogger<TrailerLedger> _logger;
    private readonly object _lock = new();
    private List<LedgerEntry>? _entries;

    public TrailerLedger(IApplicationPaths applicationPaths, ILogger<TrailerLedger> logger)
    {
        _ledgerPath = Path.Combine(applicationPaths.DataPath, "trailerdownloader", "downloads.json");
        _logger = logger;
    }

    public void Record(string filePath, Guid movieId, string sourceUrl)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => PathsEqual(e.FilePath, filePath));
            entries.Add(new LedgerEntry
            {
                FilePath = filePath,
                MovieId = movieId,
                SourceUrl = sourceUrl,
                DownloadedAtUtc = DateTime.UtcNow
            });
            Save(entries);
        }
    }

    public bool IsPluginCreated(string filePath)
    {
        lock (_lock)
        {
            return Load().Any(e => PathsEqual(e.FilePath, filePath));
        }
    }

    public void Remove(string filePath)
    {
        lock (_lock)
        {
            var entries = Load();
            if (entries.RemoveAll(e => PathsEqual(e.FilePath, filePath)) > 0)
            {
                Save(entries);
            }
        }
    }

    /// <summary>Returns all entries whose files still exist, pruning stale ones.</summary>
    public IReadOnlyList<LedgerEntry> GetAll()
    {
        lock (_lock)
        {
            var entries = Load();
            var live = entries.Where(e => File.Exists(e.FilePath)).ToList();
            if (live.Count != entries.Count)
            {
                Save(live);
            }

            return live;
        }
    }

    private List<LedgerEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(_ledgerPath))
            {
                _entries = JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(_ledgerPath)) ?? new List<LedgerEntry>();
            }
            else
            {
                _entries = new List<LedgerEntry>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read trailer ledger at {Path}, starting empty", _ledgerPath);
            _entries = new List<LedgerEntry>();
        }

        return _entries;
    }

    private void Save(List<LedgerEntry> entries)
    {
        _entries = entries;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
            File.WriteAllText(_ledgerPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write trailer ledger to {Path}", _ledgerPath);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
