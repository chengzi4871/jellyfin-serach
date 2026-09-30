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
    public string RunType { get; set; } = "none";
    public string Stage { get; set; } = "idle";
    public long CurrentVideoIndex { get; set; }
    public long CurrentVideoTotal { get; set; }
    public string? CurrentVideoTitle { get; set; }
    public long CurrentFrameCompleted { get; set; }
    public long CurrentFrameTotal { get; set; }
    public long CurrentFramesSampled { get; set; }
    public long CurrentFramesDeduplicated { get; set; }
    public long CurrentRetryAttempt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public long CurrentBatchIndex { get; set; }
    public long CurrentBatchTotal { get; set; }
    public long CurrentBatchItems { get; set; }
    public long EmbeddingRequests { get; set; }
    public long EmbeddingInputsCompleted { get; set; }
    public long EmbeddingInputsTotal { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public double InputsPerSecond { get; set; }
    public long EstimatedRemainingSeconds { get; set; }
    public double ProgressPercent => QueueTotal <= 0 ? 0 : Math.Round(QueueCompleted * 100d / QueueTotal, 2);
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
        RunType = "none";
        Stage = "idle";
        CurrentVideoIndex = 0;
        CurrentVideoTotal = 0;
        CurrentVideoTitle = null;
        CurrentFrameCompleted = 0;
        CurrentFrameTotal = 0;
        CurrentFramesSampled = 0;
        CurrentFramesDeduplicated = 0;
        CurrentRetryAttempt = 0;
        NextRetryAt = null;
        CurrentBatchIndex = 0;
        CurrentBatchTotal = 0;
        CurrentBatchItems = 0;
        EmbeddingRequests = 0;
        EmbeddingInputsCompleted = 0;
        EmbeddingInputsTotal = 0;
        StartedAt = null;
        LastProgressAt = null;
        InputsPerSecond = 0;
        EstimatedRemainingSeconds = 0;
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

    public void BeginRun(string runType, bool rebuild)
    {
        if (rebuild) ResetIndexCounters();
        RunType = runType;
        Status = "loading_library";
        Stage = "loading_library";
        QueueTotal = 0;
        QueueCompleted = 0;
        PendingVideos = 0;
        QueueWaiting = 0;
        CurrentVideoIndex = 0;
        CurrentVideoTotal = 0;
        CurrentVideoTitle = null;
        CurrentFrameCompleted = 0;
        CurrentFrameTotal = 0;
        CurrentFramesSampled = 0;
        CurrentFramesDeduplicated = 0;
        CurrentRetryAttempt = 0;
        NextRetryAt = null;
        CurrentBatchIndex = 0;
        CurrentBatchTotal = 0;
        CurrentBatchItems = 0;
        EmbeddingRequests = 0;
        EmbeddingInputsCompleted = 0;
        EmbeddingInputsTotal = 0;
        StartedAt = DateTime.UtcNow;
        LastProgressAt = StartedAt;
        InputsPerSecond = 0;
        EstimatedRemainingSeconds = 0;
    }

    public void SetQueue(long total)
    {
        QueueTotal = total;
        PendingVideos = total;
        LastProgressAt = DateTime.UtcNow;
    }

    public void SetVideo(long index, long total, string itemId, string title)
    {
        CurrentVideoIndex = index;
        CurrentVideoTotal = total;
        CurrentItemId = itemId;
        CurrentVideoTitle = title;
        CurrentFrameCompleted = 0;
        CurrentFrameTotal = 0;
        CurrentFramesSampled = 0;
        CurrentFramesDeduplicated = 0;
        CurrentRetryAttempt = 0;
        NextRetryAt = null;
        CurrentBatchIndex = 0;
        CurrentBatchTotal = 0;
        CurrentBatchItems = 0;
        Stage = "preparing_video";
        LastProgressAt = DateTime.UtcNow;
    }

    public void SetStage(string stage, long batchIndex, long batchTotal, long batchItems)
    {
        Stage = stage;
        CurrentBatchIndex = batchIndex;
        CurrentBatchTotal = batchTotal;
        CurrentBatchItems = batchItems;
        if (stage == "embedding_frames")
        {
            CurrentFrameTotal = batchItems;
            CurrentFrameCompleted = 0;
        }
        LastProgressAt = DateTime.UtcNow;
    }

    public void SetBatchProgress(long batchIndex, long batchTotal, long count)
    {
        EmbeddingRequests++;
        EmbeddingInputsCompleted += count;
        CurrentBatchIndex = batchIndex;
        CurrentBatchTotal = batchTotal;
        CurrentBatchItems = count;
        CurrentFrameCompleted = Math.Min(CurrentFrameTotal, CurrentFrameCompleted + count);
        UpdateRate();
    }

    public void DiscardPendingInputs() => EmbeddingInputsTotal = EmbeddingInputsCompleted;

    public void PlanEmbeddingInputs(long count) => EmbeddingInputsTotal += Math.Max(0, count);

    public void RecordEmbeddingInputs(long count)
    {
        EmbeddingRequests++;
        EmbeddingInputsCompleted += count;
        UpdateRate();
    }

    public void RecordVideoCompleted(bool indexed)
    {
        if (indexed) IndexedVideos++;
        QueueCompleted++;
        PendingVideos = Math.Max(0, PendingVideos - 1);
        Stage = "video_completed";
        UpdateRate();
    }

    private void UpdateRate()
    {
        if (StartedAt is not { } started) return;
        var elapsed = Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds);
        InputsPerSecond = EmbeddingInputsCompleted / elapsed;
        if (QueueCompleted >= QueueTotal) EstimatedRemainingSeconds = 0;
        else if (QueueCompleted > 0)
        {
            var videosPerSecond = QueueCompleted / elapsed;
            EstimatedRemainingSeconds = (long)Math.Ceiling((QueueTotal - QueueCompleted) / videosPerSecond);
        }
        LastProgressAt = DateTime.UtcNow;
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
    private string? _lastTriggeredWindow;

    public IndexScheduleService(IndexCoordinator coordinator) => _coordinator = coordinator;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration?.ScheduledIndexEnabled == true)
            {
                var windows = IndexSchedule.Parse(configuration.ScheduledIndexWindows);
                var active = IndexSchedule.FindActive(DateTime.Now, windows);
                if (active is { } current)
                {
                    var key = $"{current.StartAt:yyyyMMddHHmm}-{current.Window.Key}";
                    if (!string.Equals(_lastTriggeredWindow, key, StringComparison.Ordinal)
                        && _coordinator.StartScheduled())
                    {
                        _lastTriggeredWindow = key;
                    }
                }
                else
                {
                    _lastTriggeredWindow = null;
                }
            }
            else
            {
                _lastTriggeredWindow = null;
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
    }
}

public sealed class JellyfinAdapter
{
    // Jellyfin-specific item, permission and Trickplay access stays here.
    // The core scoring/index pipeline must not depend on Jellyfin runtime types.
}
