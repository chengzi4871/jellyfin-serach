using System;
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
    private bool _rebuildRequested;

    public IndexCoordinator(ILibraryManager library, VideoIndexer indexer, VisualSearchState state)
    {
        _library = library;
        _indexer = indexer;
        _state = state;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null) return;
            _state.Paused = false;
            _run = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(_run.Token));
        }
    }

    public void Rebuild()
    {
        lock (_gate)
        {
            _state.ResetIndexCounters();
            if (_run is not null)
            {
                _rebuildRequested = true;
                _state.Status = "queued_rebuild";
                _run.Cancel();
                return;
            }

            _state.Paused = false;
            _run = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(_run.Token));
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _rebuildRequested = false;
            _run?.Cancel();
            _state.Status = "cancelled";
            _state.Paused = false;
        }
    }

    public void Pause()
    {
        _state.Paused = true;
        _state.Status = "paused";
    }

    public void Resume()
    {
        _state.Paused = false;
        if (_run is not null) _state.Status = "processing";
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _state.Status = "processing";
            var videos = _library.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IsFolder = false,
                Recursive = true
            }).OfType<Video>().ToList();
            _state.QueueTotal = videos.Count;
            _state.PendingVideos = videos.Count;

            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                _state.CurrentItemId = video.Id.ToString();

                var indexed = await IndexWithRetryAsync(video, cancellationToken).ConfigureAwait(false);
                if (indexed)
                {
                    _state.IndexedVideos++;
                    _state.QueueCompleted++;
                }
                _state.PendingVideos = Math.Max(0, _state.PendingVideos - 1);
            }
            _state.Status = "ready";
        }
        catch (OperationCanceledException)
        {
            if (_state.Status != "queued_rebuild") _state.Status = "cancelled";
        }
        catch (Exception ex)
        {
            _state.RecordError(ex.Message);
            _state.Status = "error";
        }
        finally
        {
            bool restart;
            lock (_gate)
            {
                _run?.Dispose();
                _run = null;
                restart = _rebuildRequested;
                _rebuildRequested = false;
            }
            if (restart) Rebuild();
        }
    }

    private async Task<bool> IndexWithRetryAsync(Video video, CancellationToken cancellationToken)
    {
        var retryDelay = Math.Clamp(Plugin.Instance?.Configuration.IndexRetryDelaySeconds ?? 15, 1, 3600);
        var attempt = 0;
        while (true)
        {
            try
            {
                await _indexer.IndexAsync(video, string.Empty, cancellationToken).ConfigureAwait(false);
                _state.RecordError(string.Empty);
                return true;
            }
            catch (Exception ex) when (VisualSearchClient.IsRetryable(ex))
            {
                attempt++;
                _state.RetryCount++;
                _state.QueueWaiting = 1;
                _state.Status = "waiting_retry";
                _state.RecordError($"{video.Name}: retry {attempt}; {ex.Message}");
                var delay = TimeSpan.FromSeconds(Math.Min(300, retryDelay * Math.Pow(2, Math.Min(attempt - 1, 5))));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _state.QueueWaiting = 0;
                await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _state.FailedVideos++;
                _state.PermanentFailures++;
                _state.RecordError($"{video.Name}: {ex.Message}");
                _state.FailureReasons.AddOrUpdate("index_permanent_error", 1, (_, old) => old + 1);
                return false;
            }
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (_state.Paused)
        {
            _state.Status = "paused";
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
