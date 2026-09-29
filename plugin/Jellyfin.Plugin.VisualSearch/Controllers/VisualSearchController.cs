using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VisualSearch.Controllers;

[ApiController]
[Authorize]
[Route("VisualSearch")]
public sealed class VisualSearchController : ControllerBase
{
    private readonly VisualSearchState _state;

    public VisualSearchController(VisualSearchState state) => _state = state;

    [HttpGet("Health")]
    public object Health() => new { status = _state.Status, indexedVideos = _state.IndexedVideos };

    [HttpGet("IndexStatus")]
    public object IndexStatus() => _state;

    [HttpGet("Stats")]
    public object Stats() => new { indexVersion = 1, indexedVideos = _state.IndexedVideos, pending = _state.PendingVideos, failed = _state.FailedVideos };

    [HttpPost("Search")]
    public ActionResult<SearchResponse> Search([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("query is required");
        }
        // Retrieval is deliberately behind an adapter boundary in P0.
        // User identity comes from the authenticated principal, never the request body.
        return Ok(new SearchResponse(request.Query, Array.Empty<SearchResult>(), 0));
    }

    [HttpPost("Index/Incremental")]
    public IActionResult Incremental() { _state.Status = "queued"; return Accepted(); }

    [HttpPost("Index/Rebuild")]
    public IActionResult Rebuild() { _state.Status = "queued_rebuild"; return Accepted(); }

    [HttpPost("Index/Pause")]
    public IActionResult Pause() { _state.Status = "paused"; return Ok(); }

    [HttpPost("Index/Resume")]
    public IActionResult Resume() { _state.Status = "queued"; return Ok(); }

    [HttpPost("Index/Cancel")]
    public IActionResult Cancel() { _state.Status = "cancelled"; return Ok(); }
}

public sealed record SearchRequest(string Query, string[]? LibraryIds = null, int Limit = 30);
public sealed record SearchResponse(string Query, SearchResult[] Results, long ElapsedMs);
public sealed record SearchResult(string ItemId, double Score, double? VisualScore, double? TitleScore, BestFrame? BestFrame);
public sealed record BestFrame(int FrameIndex, long TimestampMs, double Score);
