using Jellyfin.Plugin.TrailerDownloader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TrailerDownloader;

/// <summary>
/// The Trailer Downloader plugin. Downloads YouTube trailers for movies in the
/// library and stores them using Jellyfin's local trailer naming conventions.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Config migration: upgrade the pre-1.1.0 default search template unless the
        // user customized it.
        if (Configuration.SearchTemplate == PluginConfiguration.LegacySearchTemplate)
        {
            Configuration.SearchTemplate = PluginConfiguration.DefaultSearchTemplate;
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Trailer Downloader";

    public override string Description =>
        "Downloads movie trailers from YouTube and saves them next to your movies so Jellyfin picks them up as local trailers.";

    public override Guid Id => Guid.Parse("8f7fd897-4d3c-4a06-b8b1-8ca9b1c1a042");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "TrailerDownloader",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "Trailer Downloader",
                MenuSection = "server",
                MenuIcon = "play_circle_filled"
            }
        };
    }
}
