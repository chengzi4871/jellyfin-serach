using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.IO;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VisualSearch.Controllers;

[ApiController]
[Authorize]
[Route("VisualSearch")]
public sealed class VisualSearchController : ControllerBase
{
    private readonly VisualSearchState _state;
    private readonly VisualSearchClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly VideoIndexer _indexer;
    private readonly IndexCoordinator _coordinator;

    public VisualSearchController(VisualSearchState state, VisualSearchClient client, ILibraryManager libraryManager, VideoIndexer indexer, IndexCoordinator coordinator)
    {
        _state = state;
        _client = client;
        _libraryManager = libraryManager;
        _indexer = indexer;
        _coordinator = coordinator;
    }

    [HttpGet("Health")]
    public async Task<ActionResult> Health(CancellationToken cancellationToken)
    {
        try
        {
            var worker = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { status = "ready", worker, indexedVideos = _state.IndexedVideos });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { status = "offline", message = "semantic search worker is offline" });
        }
    }

    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.VisualSearch.Web.visual-search.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    [HttpGet("IndexStatus")]
    public object IndexStatus() => _state;

    [HttpGet("Stats")]
    public object Stats() => new { indexVersion = 1, indexedVideos = _state.IndexedVideos, pending = _state.PendingVideos, failed = _state.FailedVideos };

    [HttpPost("Search")]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("query is required");
        }
        try
        {
            var started = DateTime.UtcNow;
            var vector = await _client.EmbedTextAsync(request.Query, cancellationToken).ConfigureAwait(false);
            var hits = await _client.SearchAsync(vector, "jellyfin_video_text", Math.Clamp(request.Limit, 1, 100), cancellationToken).ConfigureAwait(false);
            var userId = GetUserId();
            var results = hits.Select(hit =>
            {
                if (!Guid.TryParse(hit.ItemId, out var id)) return null;
                // Passing the authenticated user id makes Jellyfin perform its normal access filtering.
                var item = _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(id, userId);
                return item is null ? null : new SearchResult(hit.ItemId, item.Name, hit.Score, null, hit.Score, null);
            }).Where(x => x is not null).Cast<SearchResult>().ToArray();
            return Ok(new SearchResponse(request.Query, results, (long)(DateTime.UtcNow - started).TotalMilliseconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { status = "offline", message = "semantic search computation node is offline" });
        }
    }

    [HttpPost("Index/Incremental")]
    public IActionResult Incremental() { _coordinator.Start(); return Accepted(new { status = "queued" }); }

    [HttpPost("Index/Ensure")]
    public async Task<IActionResult> Ensure(CancellationToken cancellationToken)
    {
        await _client.EnsureCollectionsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { status = "ready" });
    }

    [HttpPost("Index/Rebuild")]
    public IActionResult Rebuild() { _coordinator.Cancel(); _state.IndexedVideos = 0; _state.FailedVideos = 0; _coordinator.Start(); return Accepted(new { status = "queued_rebuild" }); }

    [HttpPost("Index/Pause")]
    public IActionResult Pause() { _state.Status = "paused"; return Ok(); }

    [HttpPost("Index/Resume")]
    public IActionResult Resume() { _state.Status = "queued"; return Ok(); }

    [HttpPost("Index/Cancel")]
    public IActionResult Cancel() { _coordinator.Cancel(); return Ok(); }

    [HttpPost("Index/Item")]
    public async Task<ActionResult> IndexItem([FromBody] IndexItemRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ItemId, out var itemId) || _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(itemId) is not { } video)
        {
            return NotFound();
        }
        _state.Status = "processing";
        var libraryId = request.LibraryId ?? string.Empty;
        var indexed = await _indexer.IndexAsync(video, libraryId, cancellationToken).ConfigureAwait(false);
        _state.IndexedVideos++;
        _state.Status = "ready";
        return Ok(new { itemId = video.Id, textIndexed = indexed.Text, framesIndexed = indexed.Frames });
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }
}

public sealed record SearchRequest(string Query, string[]? LibraryIds = null, int Limit = 30);
public sealed record IndexItemRequest(string ItemId, string? LibraryId = null);
public sealed record SearchResponse(string Query, SearchResult[] Results, long ElapsedMs);
public sealed record SearchResult(string ItemId, string Title, double Score, double? VisualScore, double? TitleScore, BestFrame? BestFrame);
public sealed record BestFrame(int FrameIndex, long TimestampMs, double Score);
