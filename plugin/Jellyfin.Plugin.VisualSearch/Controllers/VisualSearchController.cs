using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.IO;
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
    private readonly VideoIndexer _indexer;
    private readonly IndexCoordinator _coordinator;
    private readonly ITrickplayManager _trickplay;

    public VisualSearchController(VisualSearchState state, VisualSearchClient client, ILibraryManager libraryManager, VideoIndexer indexer, IndexCoordinator coordinator, ITrickplayManager trickplay)
    {
        _state = state;
        _client = client;
        _libraryManager = libraryManager;
        _indexer = indexer;
        _coordinator = coordinator;
        _trickplay = trickplay;
    }

    [HttpGet("Health")]
    public async Task<ActionResult> Health(CancellationToken cancellationToken)
    {
        try
        {
            var cloud = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { status = "ready", cloud, indexedVideos = _state.IndexedVideos });
        }
        catch (VisualSearchHealthException ex)
        {
            return StatusCode(ex.StatusCode, new { status = "error", stage = ex.Stage, message = ex.Message });
        }
        catch (TaskCanceledException ex)
        {
            return StatusCode(504, new { status = "timeout", stage = "cloud", message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { status = "error", stage = "unknown", message = ex.Message });
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
    public object Stats() => _state;

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
            var textHitsTask = _client.SearchAsync(vector, "jellyfin_video_text", 100, cancellationToken);
            var frameHitsTask = _client.SearchAsync(vector, "jellyfin_video_frames", 500, cancellationToken);
            await Task.WhenAll(textHitsTask, frameHitsTask).ConfigureAwait(false);
            var textHits = await textHitsTask.ConfigureAwait(false);
            var frameHits = await frameHitsTask.ConfigureAwait(false);
            var titleScores = textHits.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Max(y => y.Score));
            var frameGroups = frameHits.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Score).ToList());
            var candidates = titleScores.Keys.Concat(frameGroups.Keys).Distinct().Select(itemId =>
            {
                frameGroups.TryGetValue(itemId, out var frames);
                titleScores.TryGetValue(itemId, out var titleScore);
                double? visualScore = frames is null || frames.Count == 0 ? null : 0.7 * frames[0].Score + 0.3 * frames.Take(3).Average(x => x.Score);
                var vw = Plugin.Instance?.Configuration.VisualWeight ?? 0.75;
                var tw = Plugin.Instance?.Configuration.TitleWeight ?? 0.25;
                var final = visualScore.HasValue && titleScores.ContainsKey(itemId)
                    ? (visualScore.Value * vw + titleScore * tw) / (vw + tw)
                    : visualScore ?? titleScore;
                var best = frames is { Count: > 0 } ? frames[0] : null;
                var frame = best is null ? null : new BestFrame(
                    best.Payload.TryGetProperty("frameIndex", out var fi) ? fi.GetInt32() : 0,
                    best.Payload.TryGetProperty("timestampMs", out var ts) ? ts.GetInt64() : 0,
                    best.Score);
                return (itemId, final, visualScore, title: titleScores.ContainsKey(itemId) ? titleScore : (double?)null, frame);
            }).OrderByDescending(x => x.final).Take(Math.Clamp(request.Limit, 1, 100)).ToArray();
            var userId = GetUserId();
            var results = candidates.Select(hit =>
            {
                if (!Guid.TryParse(hit.itemId, out var id)) return null;
                // Passing the authenticated user id makes Jellyfin perform its normal access filtering.
                var item = _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(id, userId);
                return item is null ? null : new SearchResult(hit.itemId, item.Name, hit.final, hit.visualScore, hit.title, hit.frame);
            }).Where(x => x is not null).Cast<SearchResult>().ToArray();
            return Ok(new SearchResponse(request.Query, results, (long)(DateTime.UtcNow - started).TotalMilliseconds));
        }
        catch (TaskCanceledException ex)
        {
            return StatusCode(504, new { status = "timeout", stage = "search", message = ex.Message });
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            return BadRequest(new { status = "error", stage = "configuration", message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { status = "error", stage = ex.Message.Contains("Qdrant", StringComparison.OrdinalIgnoreCase) ? "qdrant" : "cloud", message = ex.Message });
        }
    }

    [HttpPost("Index/Incremental")]
    public IActionResult Incremental() { _coordinator.Start(); return Accepted(new { status = "queued" }); }

    [HttpPost("Index/Ensure")]
    public async Task<IActionResult> Ensure(CancellationToken cancellationToken)
    {
        try
        {
            await _client.EnsureCollectionsAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { status = "ready" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { status = "error", stage = "qdrant", message = ex.Message });
        }
        catch (TaskCanceledException ex)
        {
            return StatusCode(504, new { status = "timeout", stage = "qdrant", message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { status = "error", stage = "qdrant", message = ex.Message });
        }
    }

    [HttpPost("Index/Rebuild")]
    public IActionResult Rebuild() { _coordinator.Rebuild(); return Accepted(new { status = "queued_rebuild" }); }

    [HttpPost("Index/Pause")]
    public IActionResult Pause() { _coordinator.Pause(); return Ok(new { status = "paused" }); }

    [HttpPost("Index/Resume")]
    public IActionResult Resume() { _coordinator.Resume(); return Ok(new { status = "resumed" }); }

    [HttpPost("Index/Cancel")]
    public IActionResult Cancel() { _coordinator.Cancel(); return Ok(); }

    [HttpPost("Index/Item")]
    public async Task<ActionResult> IndexItem([FromBody] IndexItemRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (!Guid.TryParse(request.ItemId, out var itemId) || _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(itemId, userId) is not { } video)
        {
            return NotFound();
        }
        _state.Status = "processing";
        var libraryId = request.LibraryId ?? string.Empty;
        try
        {
            var indexed = await _indexer.IndexAsync(video, libraryId, cancellationToken).ConfigureAwait(false);
            _state.IndexedVideos++;
            _state.Status = "ready";
            return Ok(new { itemId = video.Id, textIndexed = indexed.Text, framesIndexed = indexed.Frames });
        }
        catch (TaskCanceledException ex) { return StatusCode(504, new { status = "timeout", stage = "index", message = ex.Message }); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { return BadRequest(new { status = "error", stage = "configuration", message = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(503, new { status = "error", stage = "embedding", message = ex.Message }); }
    }

    [HttpPost("Test/Random")]
    public async Task<ActionResult> TestRandom([FromBody] RandomTestRequest? request, CancellationToken cancellationToken)
    {
        var count = Math.Clamp(request?.Count ?? 3, 1, 10);
        var frames = Math.Clamp(request?.FramesPerVideo ?? 2, 1, 5);
        WorkerHealth cloud;
        try
        {
            cloud = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { status = "offline", message = "cloud embedding API or Qdrant is offline" });
        }
        var videos = await GetRandomVideosWithTrickplayAsync(count, GetUserId(), cancellationToken).ConfigureAwait(false);
        var results = new System.Collections.Generic.List<EmbeddingProbeResult>();
        foreach (var video in videos)
        {
            try
            {
                results.Add(await _indexer.ProbeAsync(video, frames, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                results.Add(new EmbeddingProbeResult(video.Id.ToString(), video.Name, false, 0, ex.Message));
            }
        }
        return Ok(new { status = "completed", cloud, requestedVideos = count, testedVideos = results.Count, framesPerVideo = frames, results });
    }

    /// <summary>Returns the exact title text and individual frame images used for embedding, plus query-to-frame cosine scores.</summary>
    [HttpPost("Test/Inspect")]
    public async Task<ActionResult> Inspect([FromBody] InspectRequest? request, CancellationToken cancellationToken)
    {
        var count = Math.Clamp(request?.Count ?? 3, 1, 10);
        var frames = Math.Clamp(request?.FramesPerVideo ?? 3, 1, 8);
        var queries = (request?.Queries ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var videos = new List<MediaBrowser.Controller.Entities.Video>();
        foreach (var rawId in request?.ItemIds ?? Array.Empty<string>())
        {
            if (Guid.TryParse(rawId, out var id) && _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(id, GetUserId()) is { } video) videos.Add(video);
        }
        if (videos.Count == 0)
        {
            videos = (await GetRandomVideosWithTrickplayAsync(count, GetUserId(), cancellationToken).ConfigureAwait(false)).ToList();
        }
        var results = new List<DetailedProbeResult>();
        foreach (var video in videos.Take(count))
        {
            try { results.Add(await _indexer.ProbeDetailedAsync(video, frames, queries, cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) { results.Add(new DetailedProbeResult(video.Id.ToString(), video.Name, $"ERROR: {ex.Message}", new VectorSummary(0, 0, 0, 0, 0, Array.Empty<double>()), new Dictionary<string, double>(), Array.Empty<ProbeFrame>(), queries)); }
        }
        return Ok(new { status = "completed", count = results.Count, queries, results });
    }

    /// <summary>Chooses one video and extracts the exact title and cropped Trickplay frames without calling the cloud model.</summary>
    [HttpPost("Test/Preview")]
    public async Task<ActionResult> Preview([FromBody] PreviewRequest? request, CancellationToken cancellationToken)
    {
        MediaBrowser.Controller.Entities.Video? video = null;
        var userId = GetUserId();
        if (Guid.TryParse(request?.ItemId, out var requestedId))
            video = _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(requestedId, userId);
        video ??= (await GetRandomVideosWithTrickplayAsync(1, userId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (video is null) return NotFound("No video is available for semantic acceptance");
        var frames = Math.Clamp(request?.FramesPerVideo ?? 3, 1, 8);
        var result = await _indexer.PreviewAsync(video, frames, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    private async Task<IReadOnlyList<MediaBrowser.Controller.Entities.Video>> GetRandomVideosWithTrickplayAsync(int count, Guid userId, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<Guid>();
        const int pageSize = 1000;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await _trickplay.GetTrickplayItemsAsync(pageSize, offset).ConfigureAwait(false);
            foreach (var info in page) candidates.Add(info.ItemId);
            if (page.Count < pageSize) break;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return candidates.OrderBy(_ => Random.Shared.Next()).Select(id => _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(id, userId)).Where(x => x is not null).Cast<MediaBrowser.Controller.Entities.Video>().Take(count).ToArray();
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }
}

public sealed record SearchRequest(string Query, string[]? LibraryIds = null, int Limit = 30);
public sealed record IndexItemRequest(string ItemId, string? LibraryId = null);
public sealed record RandomTestRequest(int Count = 3, int FramesPerVideo = 2);
public sealed record InspectRequest(int Count = 3, int FramesPerVideo = 3, string[]? Queries = null, string[]? ItemIds = null);
public sealed record PreviewRequest(string? ItemId = null, int FramesPerVideo = 3);
public sealed record SearchResponse(string Query, SearchResult[] Results, long ElapsedMs);
public sealed record SearchResult(string ItemId, string Title, double Score, double? VisualScore, double? TitleScore, BestFrame? BestFrame);
public sealed record BestFrame(int FrameIndex, long TimestampMs, double Score);
