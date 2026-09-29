using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<VisualSearchState>();
        serviceCollection.AddSingleton<JellyfinAdapter>();
    }
}

public sealed class VisualSearchState
{
    public string Status { get; set; } = "not_started";
    public long IndexedVideos { get; set; }
    public long PendingVideos { get; set; }
    public long FailedVideos { get; set; }
}

public sealed class JellyfinAdapter
{
    // Jellyfin-specific item, permission and Trickplay access stays here.
    // The core scoring/index pipeline must not depend on Jellyfin runtime types.
}
