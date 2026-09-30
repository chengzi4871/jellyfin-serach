using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Jellyfin.Plugin.VisualSearch.Web;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<VisualSearchState>();
        serviceCollection.AddSingleton<JellyfinAdapter>();
        serviceCollection.AddSingleton<VideoIndexer>();
        serviceCollection.AddSingleton<IndexCoordinator>();
        serviceCollection.AddHostedService<IndexScheduleService>();
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlScriptStartupFilter>();
        serviceCollection.AddHttpClient<VisualSearchClient>((provider, client) =>
        {
            var seconds = Math.Clamp(Plugin.Instance?.Configuration.EmbeddingTimeoutSeconds ?? 60, 5, 300);
            client.Timeout = System.TimeSpan.FromSeconds(seconds);
        }).ConfigurePrimaryHttpMessageHandler(() =>
        {
            var seconds = Math.Clamp(Plugin.Instance?.Configuration.EmbeddingTimeoutSeconds ?? 60, 5, 300);
            return new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = System.TimeSpan.FromSeconds(Math.Min(seconds, 30)),
                PooledConnectionLifetime = System.TimeSpan.FromMinutes(5)
            };
        });
    }
}

public sealed class VisualSearchState
{
    public string Status { get; set; } = "not_started";
    public long IndexedVideos { get; set; }
    public long PendingVideos { get; set; }
    public long FailedVideos { get; set; }
    public long VideosWithFrames { get; set; }
    public long VideosWithoutFrames { get; set; }
    public long FramesRead { get; set; }
    public long FrameReadFailures { get; set; }
    public long RetryCount { get; set; }
    public long QueueTotal { get; set; }
    public long QueueCompleted { get; set; }
    public long QueueWaiting { get; set; }
    public long PermanentFailures { get; set; }
    public string? CurrentItemId { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public bool Paused { get; set; }
    public ConcurrentDictionary<string, long> FrameFailureReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, long> QueueFailureReasons { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ResetIndexCounters()
    {
        IndexedVideos = 0;
        PendingVideos = 0;
        FailedVideos = 0;
        VideosWithFrames = 0;
        VideosWithoutFrames = 0;
        FramesRead = 0;
        FrameReadFailures = 0;
        RetryCount = 0;
        QueueTotal = 0;
        QueueCompleted = 0;
        QueueWaiting = 0;
        PermanentFailures = 0;
        CurrentItemId = null;
        LastError = null;
        LastErrorAt = null;
        FrameFailureReasons.Clear();
        QueueFailureReasons.Clear();
    }

    public void RecordFrameSuccess(int count)
    {
        VideosWithFrames++;
        FramesRead += count;
    }

    public void RecordFrameFailure(string reason, int count = 1)
    {
        VideosWithoutFrames++;
        FrameReadFailures += count;
        FrameFailureReasons.AddOrUpdate(reason, count, (_, old) => old + count);
    }

    public void RecordError(string message)
    {
        LastError = message;
        LastErrorAt = DateTime.UtcNow;
    }
}

public sealed class IndexScheduleService : BackgroundService
{
    private readonly IndexCoordinator _coordinator;

    public IndexScheduleService(IndexCoordinator coordinator) => _coordinator = coordinator;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = Math.Clamp(Plugin.Instance?.Configuration.ScheduledIndexIntervalMinutes ?? 360, 5, 10080);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;
            if (Plugin.Instance?.Configuration.ScheduledIndexEnabled == true) _coordinator.Start();
        }
    }
}

public sealed class JellyfinAdapter
{
    // Jellyfin-specific item, permission and Trickplay access stays here.
    // The core scoring/index pipeline must not depend on Jellyfin runtime types.
}
