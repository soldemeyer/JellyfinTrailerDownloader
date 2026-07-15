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
    public bool HasLocalTrailer(Movie movie)
    {
        var folder = movie.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        if (movie.IsInMixedFolder)
        {
            // Only this movie's own suffixed trailer counts in a shared folder.
            var baseName = Path.GetFileNameWithoutExtension(movie.Path);
            return Directory.EnumerateFiles(folder, baseName + "-trailer.*").Any();
        }

        if (Directory.EnumerateFiles(folder).Any(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            return name.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "trailer", StringComparison.OrdinalIgnoreCase);
        }))
        {
            return true;
        }

        var trailersFolder = Path.Combine(folder, "trailers");
        return Directory.Exists(trailersFolder) && Directory.EnumerateFiles(trailersFolder).Any();
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

    /// <summary>Builds the download target (folder + final base file name) for a movie.</summary>
    public TrailerDownloadRequest? BuildRequest(Movie movie, PluginConfiguration config, string url)
    {
        var folder = movie.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        // The trailers-subfolder layout requires the movie to own its folder.
        if (config.FileLayout == TrailerFileLayout.TrailersFolder && !movie.IsInMixedFolder)
        {
            var title = Sanitize(movie.Name);
            if (movie.ProductionYear is not null)
            {
                title = $"{title} ({movie.ProductionYear})";
            }

            return new TrailerDownloadRequest(url, Path.Combine(folder, "trailers"), title + " Trailer");
        }

        var baseName = Path.GetFileNameWithoutExtension(movie.Path);
        return new TrailerDownloadRequest(url, folder, baseName + "-trailer");
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
