using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrailerDownloader.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TrailerDownloader.Services;

/// <summary>Per-movie trailer status as shown in the plugin UI.</summary>
public record MovieTrailerInfo(Guid Id, string Name, int? Year, bool HasLocalTrailer, bool HasRemoteTrailerUrl, bool CanSearch);

/// <summary>
/// Enumerates library movies, determines trailer URLs (metadata first, YouTube search
/// fallback second) and computes Jellyfin-convention target paths.
/// </summary>
public class TrailerScanner
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly ILibraryManager _libraryManager;

    public TrailerScanner(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<Movie> GetMovies()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        })
        .OfType<Movie>()
        .Where(m => !string.IsNullOrEmpty(m.Path))
        .OrderBy(m => m.SortName, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    public Movie? GetMovie(Guid id) => _libraryManager.GetItemById(id) as Movie;

    public MovieTrailerInfo GetInfo(Movie movie, PluginConfiguration config)
    {
        return new MovieTrailerInfo(
            movie.Id,
            movie.Name,
            movie.ProductionYear,
            HasLocalTrailer(movie),
            GetRemoteTrailerUrl(movie) is not null,
            config.EnableSearchFallback);
    }

    /// <summary>
    /// Checks the file system for an existing trailer under either Jellyfin convention:
    /// a "-trailer" suffixed file, a bare "trailer.*" file, or a non-empty "trailers" folder.
    /// </summary>
    public bool HasLocalTrailer(Movie movie) => GetTrailerFilePaths(movie).Count > 0;

    /// <summary>Lists all existing trailer files for a movie across Jellyfin's conventions.</summary>
    public IReadOnlyList<string> GetTrailerFilePaths(Movie movie)
    {
        var folder = movie.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();

        if (movie.IsInMixedFolder)
        {
            // Only this movie's own suffixed trailers count in a shared folder
            // (covers "<base>-trailer.*" and "<base> [N]-trailer.*").
            var baseName = Path.GetFileNameWithoutExtension(movie.Path);
            results.AddRange(Directory.EnumerateFiles(folder, baseName + "*")
                .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("-trailer", StringComparison.OrdinalIgnoreCase)));
            return results;
        }

        results.AddRange(Directory.EnumerateFiles(folder).Where(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            return name.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "trailer", StringComparison.OrdinalIgnoreCase);
        }));

        var trailersFolder = Path.Combine(folder, "trailers");
        if (Directory.Exists(trailersFolder))
        {
            results.AddRange(Directory.EnumerateFiles(trailersFolder));
        }

        return results;
    }

    /// <summary>Returns the trailer URL from metadata, preferring YouTube links.</summary>
    public string? GetRemoteTrailerUrl(Movie movie)
    {
        var trailers = movie.RemoteTrailers;
        if (trailers is null || trailers.Count == 0)
        {
            return null;
        }

        var youtube = trailers.FirstOrDefault(t =>
            t.Url is not null && (t.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                               || t.Url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)));

        return (youtube ?? trailers[0])?.Url;
    }

    /// <summary>
    /// Resolves what to hand yt-dlp: the metadata trailer URL, or a ytsearch expression
    /// if the fallback is enabled. Null when the movie has no obtainable trailer.
    /// </summary>
    public string? ResolveDownloadUrl(Movie movie, PluginConfiguration config)
    {
        var url = GetRemoteTrailerUrl(movie);
        if (url is not null)
        {
            return url;
        }

        return BuildSearchExpression(movie, config);
    }

    /// <summary>
    /// Builds the yt-dlp "ytsearch1:" expression for a movie, or null when the search
    /// fallback is disabled.
    /// </summary>
    public string? BuildSearchExpression(Movie movie, PluginConfiguration config)
    {
        if (!config.EnableSearchFallback)
        {
            return null;
        }

        var query = config.SearchTemplate
            .Replace("{title}", movie.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", movie.ProductionYear?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return "ytsearch1:" + query;
    }

    /// <summary>
    /// Builds the download target (folder + final base file name) for a movie. When
    /// <paramref name="additional"/> is true (or the primary name is taken and overwrite is
    /// off), an indexed name like "Movie [2]-trailer" is used so multiple trailers coexist.
    /// </summary>
    public TrailerDownloadRequest? BuildRequest(Movie movie, PluginConfiguration config, string url, bool additional = false)
    {
        var folder = movie.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        string targetFolder;
        string baseName;

        // The trailers-subfolder layout requires the movie to own its folder.
        if (config.FileLayout == TrailerFileLayout.TrailersFolder && !movie.IsInMixedFolder)
        {
            var title = Sanitize(movie.Name);
            if (movie.ProductionYear is not null)
            {
                title = $"{title} ({movie.ProductionYear})";
            }

            targetFolder = Path.Combine(folder, "trailers");
            baseName = title + " Trailer";
        }
        else
        {
            targetFolder = folder;
            baseName = Path.GetFileNameWithoutExtension(movie.Path) + "-trailer";
        }

        if (additional)
        {
            baseName = NextFreeName(targetFolder, baseName);
        }

        return new TrailerDownloadRequest(url, targetFolder, baseName);
    }

    /// <summary>
    /// Returns the first unused variant of a trailer base name: the name itself, then
    /// "Movie [2]-trailer", "Movie [3]-trailer", … (the index is inserted before the
    /// "-trailer" suffix so Jellyfin still recognizes the file as a trailer).
    /// </summary>
    internal static string NextFreeName(string folder, string baseName)
    {
        if (!NameTaken(folder, baseName))
        {
            return baseName;
        }

        var stem = baseName.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^"-trailer".Length]
            : baseName;
        var suffix = baseName.Length == stem.Length ? string.Empty : "-trailer";

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{stem} [{i}]{suffix}";
            if (!NameTaken(folder, candidate))
            {
                return candidate;
            }
        }

        return baseName;

        static bool NameTaken(string folder, string name) =>
            Directory.Exists(folder) && Directory.EnumerateFiles(folder, name + ".*").Any();
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
