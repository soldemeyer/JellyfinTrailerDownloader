using Jellyfin.Plugin.TrailerDownloader.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TrailerDownloader;

/// <summary>Registers the plugin's services with Jellyfin's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpBinaryManager>();
        serviceCollection.AddSingleton<YtDlpBackend>();
        serviceCollection.AddSingleton<YoutubeDlMaterialBackend>();
        serviceCollection.AddSingleton<TrailerScanner>();
        serviceCollection.AddSingleton<TrailerDownloadService>();
        serviceCollection.AddHostedService<ScheduleSyncService>();
    }
}
