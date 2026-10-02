using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class IndexCoordinator
{
    private readonly ILibraryManager _library;
    private readonly VideoIndexer _indexer;
    private readonly VisualSearchState _state;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private bool _scheduledRun;
    private bool _pendingManual;
    private bool _pendingRebuild;

    public IndexCoordinator(ILibraryManager library, VideoIndexer indexer, VisualSearchState state)
    {
        _library = library;
        _indexer = indexer;
        _state = state;
    }

    /// <summary>Starts a manual incremental run immediately, regardless of schedule windows.</summary>
    public void Start() => RequestStart(scheduled: false, rebuild: false);

    /// <summary>Starts a scheduled incremental run only when the scheduler has entered a window.</summary>
    public bool StartScheduled()
    {
        lock (_gate)
        {
            if (_run is not null) return false;
            StartLocked(scheduled: true, rebuild: false);
            return true;
        }
    }

    /// <summary>Starts a manual full rebuild immediately. A scheduled run is cancelled first.</summary>
    public void Rebuild() => RequestStart(scheduled: false, rebuild: true);

    private void RequestStart(bool scheduled, bool rebuild)
    {
        lock (_gate)
        {
            if (_run is null)
            {
                StartLocked(scheduled, rebuild);
                return;
            }

            // A scheduled request never interrupts a manual run or another scheduled run.
            // Manual requests have priority and cancel a waiting scheduled retry/window.
            if (scheduled) return;
            _pendingManual = true;
            _pendingRebuild |= rebuild;
            _state.Status = rebuild ? "queued_rebuild" : "queued_manual";
            _state.Stage = _state.Status;
            _run.Cancel();
        }
    }

    private void StartLocked(bool scheduled, bool rebuild)
    {
        _state.BeginRun(scheduled ? "scheduled_incremental" : rebuild ? "manual_rebuild" : "manual_incremental", rebuild);
        _state.Paused = false;
        _scheduledRun = scheduled;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        _ = Task.Run(() => RunAsync(token, scheduled, rebuild));
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pendingManual = false;
            _pendingRebuild = false;
            _run?.Cancel();
            _state.Status = "cancelled";
            _state.Stage = "cancelled";
            _state.QueueWaiting = 0;
            _state.NextRetryAt = null;
            _state.Paused = false;
        }
    }

    public void Pause()
    {
        _state.Paused = true;
        _state.Status = "paused";
        _state.Stage = "paused";
    }

    public void Resume()
    {
        _state.Paused = false;
        if (_run is not null) _state.Status = _scheduledRun ? "processing_scheduled" : "processing";
    }

    private async Task RunAsync(CancellationToken cancellationToken, bool scheduled, bool rebuild)
    {
        try
        {
            _state.Status = scheduled ? "processing_scheduled" : "processing";
            // Capture the upper bound before querying Jellyfin. Anything that
            // changes after this instant is deliberately left for the next
            // run, so a multi-hour indexing pass cannot lose new files.
            var snapshotUpperBoundUtc = DateTime.UtcNow;
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var storedWatermark = configuration.IncrementalIndexWatermarkUtc;
            var watermark = storedWatermark.HasValue ? IndexFingerprint.NormalizeUtc(storedWatermark.Value) : (DateTime?)null;
            if (watermark.HasValue && watermark.Value > snapshotUpperBoundUtc.AddDays(1)) watermark = null;
            var configurationHash = IndexFingerprint.ComputeConfigurationHash(configuration);
            var configurationChanged = !string.Equals(configuration.IncrementalIndexConfigurationHash, configurationHash, StringComparison.Ordinal);

            var allVideos = _library.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IsFolder = false,
                Recursive = true
            }).OfType<Video>().ToList();

            var videos = new List<Video>();
            var anomalousDateModifiedVideos = 0L;
            var forceFullIndex = rebuild || !watermark.HasValue;
            string selectionReason;
            if (rebuild)
            {
                videos.AddRange(allVideos);
                selectionReason = "完整重建：选中全部视频";
            }
            else if (!watermark.HasValue)
            {
                videos.AddRange(allVideos);
                selectionReason = storedWatermark.HasValue
                    ? "增量：持久化水位线无效，执行一次安全基线扫描"
                    : "增量：尚无成功扫描水位线，执行首次基线扫描";
            }
            else if (configurationChanged)
            {
                videos.AddRange(allVideos);
                selectionReason = "增量：索引参数或 Embedding 配置已变化，复核全部视频的内容指纹";
            }
            else
            {
                foreach (var video in allVideos)
                {
                    var modifiedUtc = IndexFingerprint.NormalizeUtc(video.DateModified);
                    if (IndexFingerprint.IsPlausibleDateModified(modifiedUtc, snapshotUpperBoundUtc))
                    {
                        if (modifiedUtc > watermark.Value && modifiedUtc <= snapshotUpperBoundUtc)
                            videos.Add(video);
                        continue;
                    }

                    // A broken future/invalid DateModified must not make a
                    // video appear changed forever. Compare its persisted
                    // content signature instead; this also admits a new item
                    // whose clock value is already corrupt.
                    anomalousDateModifiedVideos++;
                    if (!await _indexer.IsContentCurrentAsync(video, cancellationToken).ConfigureAwait(false))
                        videos.Add(video);
                }
                selectionReason = "增量：DateModified > 持久化水位线且不超过本轮快照；异常时间值按内容指纹复核";
            }

            _state.SetQueue(videos.Count, selectionReason, snapshotUpperBoundUtc, watermark, anomalousDateModifiedVideos);
            var runSucceeded = true;

            for (var index = 0; index < videos.Count; index++)
            {
                var video = videos[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (scheduled) await WaitForScheduleWindowAsync(cancellationToken).ConfigureAwait(false);
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                _state.SetVideo(index + 1, videos.Count, video.Id.ToString(), video.Name);

                var libraryId = _library.GetCollectionFolders(video).FirstOrDefault()?.Id.ToString() ?? string.Empty;
                var result = await IndexWithRetryAsync(video, libraryId, cancellationToken, forceFullIndex).ConfigureAwait(false);
                runSucceeded &= result.Success;
                _state.RecordVideoCompleted(result);
            }

            if (runSucceeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PersistWatermark(snapshotUpperBoundUtc);
                _state.Stage = "completed";
                _state.Status = "ready";
            }
            else
            {
                // Keep the old watermark. The next incremental run is
                // intentionally re-entrant and retries failed videos.
                _state.Stage = "completed_with_errors";
                _state.Status = "completed_with_errors";
            }
        }
        catch (OperationCanceledException)
        {
            if (_state.Status is not ("queued_rebuild" or "queued_manual")) _state.Status = "cancelled";
        }
        catch (Exception ex)
        {
            _state.RecordError(ex.Message);
            _state.Stage = "error";
            _state.Status = "error";
        }
        finally
        {
            lock (_gate)
            {
                _run?.Dispose();
                _run = null;
                _scheduledRun = false;

                if (_pendingManual)
                {
                    var rebuild = _pendingRebuild;
                    _pendingManual = false;
                    _pendingRebuild = false;
                    StartLocked(scheduled: false, rebuild: rebuild);
                }
            }
        }
    }

    private async Task<IndexResult> IndexWithRetryAsync(Video video, string libraryId, CancellationToken cancellationToken, bool forceRebuild)
    {
        var retryDelay = Math.Clamp(Plugin.Instance?.Configuration.IndexRetryDelaySeconds ?? 15, 1, 3600);
        var attempt = 0;
        while (true)
        {
            try
            {
                var result = await _indexer.IndexAsync(video, libraryId, cancellationToken, forceRebuild).ConfigureAwait(false);
                _state.RecordError(string.Empty);
                _state.QueueWaiting = 0;
                _state.CurrentRetryAttempt = 0;
                _state.NextRetryAt = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (VisualSearchClient.IsRetryable(ex))
            {
                attempt++;
                _state.RetryCount++;
                _state.CurrentRetryAttempt = attempt;
                _state.QueueWaiting = 1;
                _state.Status = "waiting_retry";
                _state.Stage = "waiting_retry";
                _state.RecordError($"{video.Name}: retry {attempt}; {ex.Message}");
                var delay = TimeSpan.FromSeconds(Math.Min(300, retryDelay * Math.Pow(2, Math.Min(attempt - 1, 5))));
                _state.NextRetryAt = DateTime.UtcNow + delay;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _state.QueueWaiting = 0;
                _state.NextRetryAt = null;
                if (_scheduledRun) await WaitForScheduleWindowAsync(cancellationToken).ConfigureAwait(false);
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _state.FailedVideos++;
                _state.PermanentFailures++;
                _state.RecordError($"{video.Name}: {ex.Message}");
                _state.QueueFailureReasons.AddOrUpdate("index_permanent_error", 1, (_, old) => old + 1);
                return new IndexResult(false, 0, false, false, false);
            }
        }
    }

    private void PersistWatermark(DateTime snapshotUpperBoundUtc)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return;
        plugin.Configuration.IncrementalIndexWatermarkUtc = snapshotUpperBoundUtc;
        plugin.Configuration.IncrementalIndexConfigurationHash = IndexFingerprint.ComputeConfigurationHash(plugin.Configuration);
        plugin.SaveConfiguration();
        _state.SetWatermarkPersisted(snapshotUpperBoundUtc);
    }

    private async Task WaitForScheduleWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var configuration = Plugin.Instance?.Configuration;
            var windows = IndexSchedule.Parse(configuration?.ScheduledIndexWindows);
            if (configuration?.ScheduledIndexEnabled == true && IndexSchedule.FindActive(DateTime.Now, windows) is not null)
                return;
            _state.Status = "waiting_schedule";
            _state.Stage = "waiting_schedule";
            _state.QueueWaiting = 0;
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (_state.Paused)
        {
            _state.Status = "paused";
            _state.Stage = "paused";
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
