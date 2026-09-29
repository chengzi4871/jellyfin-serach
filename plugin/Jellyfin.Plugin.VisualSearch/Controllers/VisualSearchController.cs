using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.IO;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
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
    private readonly ITrickplayManager _trickplayManager;

    public VisualSearchController(VisualSearchState state, VisualSearchClient client, ILibraryManager libraryManager, ITrickplayManager trickplayManager)
    {
        _state = state;
        _client = client;
        _libraryManager = libraryManager;
        _trickplayManager = trickplayManager;
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
            var userId = User.GetUserId();
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
    public IActionResult Incremental() { _state.Status = "queued"; return Accepted(); }

    [HttpPost("Index/Ensure")]
    public async Task<IActionResult> Ensure(CancellationToken cancellationToken)
    {
        await _client.EnsureCollectionsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { status = "ready" });
    }

    [HttpPost("Index/Rebuild")]
    public IActionResult Rebuild() { _state.Status = "queued_rebuild"; return Accepted(); }

    [HttpPost("Index/Pause")]
    public IActionResult Pause() { _state.Status = "paused"; return Ok(); }

    [HttpPost("Index/Resume")]
    public IActionResult Resume() { _state.Status = "queued"; return Ok(); }

    [HttpPost("Index/Cancel")]
    public IActionResult Cancel() { _state.Status = "cancelled"; return Ok(); }

    [HttpPost("Index/Item")]
    public async Task<ActionResult> IndexItem([FromBody] IndexItemRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ItemId, out var itemId) || _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(itemId) is not { } video)
        {
            return NotFound();
        }
        _state.Status = "processing";
        var libraryId = request.LibraryId ?? string.Empty;
        var text = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        await _client.UpsertAsync("jellyfin_video_text", new[]
        {
            new { id = video.Id.ToString(), vector = await _client.EmbedTextAsync(text, cancellationToken), payload = new { itemId = video.Id.ToString(), libraryId, textHash = text.GetHashCode().ToString() } }
        }, cancellationToken).ConfigureAwait(false);

        var manifest = await _trickplayManager.GetTrickplayManifest(video).ConfigureAwait(false);
        var points = new List<object>();
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = Math.Min(Plugin.Instance?.Configuration.FramesPerVideo ?? 12, info.ThumbnailCount);
            for (var i = 0; i < samples; i++)
            {
                var frame = samples == 1 ? 0 : (int)Math.Round(i * (info.ThumbnailCount - 1d) / (samples - 1));
                var capacity = info.TileWidth * info.TileHeight;
                var tileIndex = frame / capacity;
                var tilePath = await _trickplayManager.GetTrickplayTilePathAsync(video, info.Width, tileIndex, false).ConfigureAwait(false);
                if (!System.IO.File.Exists(tilePath)) continue;
                var vector = await _client.EmbedImageAsync(await System.IO.File.ReadAllBytesAsync(tilePath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                points.Add(new { id = DeterministicId(video.Id, frame), vector, payload = new { itemId = video.Id.ToString(), libraryId, frameIndex = frame, timestampMs = frame * info.Interval } });
            }
        }
        if (points.Count > 0) await _client.UpsertAsync("jellyfin_video_frames", points, cancellationToken).ConfigureAwait(false);
        _state.IndexedVideos++;
        _state.Status = "ready";
        return Ok(new { itemId = video.Id, textIndexed = true, framesIndexed = points.Count });
    }

    private static string DeterministicId(Guid itemId, int frame)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{itemId:N}:{frame}"));
        return new Guid(bytes).ToString();
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
