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
    private CancellationTokenSource? _run;

    public IndexCoordinator(ILibraryManager library, VideoIndexer indexer, VisualSearchState state)
    {
        _library = library;
        _indexer = indexer;
        _state = state;
    }

    public void Start()
    {
        if (_run is not null) return;
        _run = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_run.Token));
    }

    public void Cancel()
    {
        _run?.Cancel();
        _run = null;
        _state.Status = "cancelled";
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
            _state.PendingVideos = videos.Count;
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _indexer.IndexAsync(video, string.Empty, cancellationToken).ConfigureAwait(false);
                    _state.IndexedVideos++;
                    _state.PendingVideos--;
                }
                catch
                {
                    _state.FailedVideos++;
                    _state.PendingVideos--;
                }
            }
            _state.Status = "ready";
        }
        catch (OperationCanceledException) { }
        finally { _run = null; }
    }
}
