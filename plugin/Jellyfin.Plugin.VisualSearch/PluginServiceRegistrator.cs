using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Jellyfin.Plugin.VisualSearch.Web;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<VisualSearchState>();
        serviceCollection.AddSingleton<JellyfinAdapter>();
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlScriptStartupFilter>();
        serviceCollection.AddHttpClient<VisualSearchClient>((provider, client) =>
        {
            client.BaseAddress = new System.Uri(Plugin.Instance?.Configuration.WorkerUrl ?? "http://127.0.0.1:8099");
            client.Timeout = System.TimeSpan.FromSeconds(3);
        });
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
