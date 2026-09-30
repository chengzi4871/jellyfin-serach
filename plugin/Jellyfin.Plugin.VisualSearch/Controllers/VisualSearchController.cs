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
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<VisualSearchController> _logger;

    public VisualSearchController(VisualSearchState state, VisualSearchClient client, ILibraryManager libraryManager, VideoIndexer indexer, IndexCoordinator coordinator, ITrickplayManager trickplay, ILogger<VisualSearchController> logger)
    {
        _state = state;
        _client = client;
        _libraryManager = libraryManager;
        _indexer = indexer;
        _coordinator = coordinator;
        _trickplay = trickplay;
        _logger = logger;
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

    [HttpGet("SearchPresets")]
    public ActionResult SearchPresets()
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var visible = Jellyfin.Plugin.VisualSearch.SearchPresets.GetVisible(configuration);
        return Ok(new
        {
            presets = visible,
            defaultPresetId = configuration.DefaultSearchPresetId,
            resultLimit = Math.Clamp(configuration.SearchResultLimit, 1, 100),
            customCode = configuration.SearchCustomCode,
            display = new
            {
                bestFramePoster = configuration.SearchShowBestFramePoster,
                bestFrameTimestamp = configuration.SearchShowBestFrameTimestamp,
                scoreBreakdown = configuration.SearchShowScoreBreakdown,
                rank = configuration.SearchShowRank,
                playAll = configuration.SearchShowPlayAll,
                queueAction = configuration.SearchShowQueueAction
            }
        });
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
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var preset = Jellyfin.Plugin.VisualSearch.SearchPresets.Resolve(configuration, request.PresetId);
            if (string.Equals(preset.Id, "balanced", StringComparison.OrdinalIgnoreCase))
            {
                preset = preset with
                {
                    VisualWeight = Math.Max(0, configuration.VisualWeight),
                    TitleWeight = Math.Max(0, configuration.TitleWeight),
                    MissingModalityPenalty = Math.Clamp(configuration.MissingModalityPenalty, 0, 1)
                };
            }
            var resultLimit = Math.Clamp(request.Limit > 0 ? request.Limit : configuration.SearchResultLimit, 1, 100);
            var vector = await _client.EmbedQueryTextAsync(request.Query, cancellationToken).ConfigureAwait(false);
            var textHitsTask = _client.SearchAsync(vector, "jellyfin_video_text", 100, cancellationToken, request.LibraryIds);
            var frameHitsTask = _client.SearchAsync(vector, "jellyfin_video_frames", 500, cancellationToken, request.LibraryIds);
            await Task.WhenAll(textHitsTask, frameHitsTask).ConfigureAwait(false);
            var textHits = await textHitsTask.ConfigureAwait(false);
            var frameHits = await frameHitsTask.ConfigureAwait(false);
            var titleHits = NormalizeHits(textHits);
            var visualHits = NormalizeHits(frameHits);
            var titleScores = titleHits.GroupBy(x => x.Hit.ItemId).ToDictionary(x => x.Key, x => x.Max(y => y.NormalizedScore));
            var frameGroups = visualHits.GroupBy(x => x.Hit.ItemId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.NormalizedScore).ToList());
            var candidates = titleScores.Keys.Concat(frameGroups.Keys).Distinct().Select(itemId =>
            {
                frameGroups.TryGetValue(itemId, out var frames);
                titleScores.TryGetValue(itemId, out var titleScore);
                double? visualScore = frames is null || frames.Count == 0 ? null : AggregateVisualScore(frames);
                var hasVisual = visualScore.HasValue;
                var hasTitle = titleScores.ContainsKey(itemId);
                var final = CalculateFinalScore(preset, visualScore, hasTitle ? titleScore : null);
                var best = frames is { Count: > 0 } ? frames[0].Hit : null;
                var frame = best is null ? null : new BestFrame(
                    best.Payload.TryGetProperty("frameIndex", out var fi) ? fi.GetInt32() : 0,
                    best.Payload.TryGetProperty("timestampMs", out var ts) ? ts.GetInt64() : 0,
                    best.Score);
                return (itemId, final, visualScore, title: hasTitle ? titleScore : (double?)null, frame, hasVisual, hasTitle);
            });
            var ordered = preset.SortBy.ToLowerInvariant() switch
            {
                "visual" => candidates.OrderByDescending(x => x.visual ?? -1).ThenByDescending(x => x.final),
                "title" => candidates.OrderByDescending(x => x.title ?? -1).ThenByDescending(x => x.final),
                "timestamp" => candidates.OrderBy(x => x.frame?.TimestampMs ?? long.MaxValue).ThenByDescending(x => x.final),
                "title-asc" => candidates.OrderBy(x => x.itemId, StringComparer.OrdinalIgnoreCase),
                _ => candidates.OrderByDescending(x => x.final).ThenBy(x => x.itemId, StringComparer.OrdinalIgnoreCase)
            };
            var selected = ordered.Take(resultLimit).ToArray();
            var userId = GetUserId();
            var results = selected.Select(hit =>
            {
                if (!Guid.TryParse(hit.itemId, out var id)) return null;
                // Passing the authenticated user id makes Jellyfin perform its normal access filtering.
                var item = _libraryManager.GetItemById<MediaBrowser.Controller.Entities.Video>(id, userId);
                if (item is null) return null;
                return new SearchResult(hit.itemId, item.Name, hit.final, hit.visual, hit.title, hit.frame)
                {
                    RunTimeTicks = item.RunTimeTicks,
                    Type = "Video",
                    HasVisualMatch = hit.hasVisual,
                    HasTitleMatch = hit.hasTitle,
                    MatchKind = hit.hasVisual && hit.hasTitle ? "both" : hit.hasVisual ? "visual" : "title"
                };
            }).Where(x => x is not null).Cast<SearchResult>().ToArray();
            return Ok(new SearchResponse(request.Query, results, (long)(DateTime.UtcNow - started).TotalMilliseconds)
            {
                PresetId = preset.Id,
                PresetName = preset.Name,
                ResultLimit = resultLimit
            });
        }
        catch (TaskCanceledException ex)
        {
            return StatusCode(504, new { status = "timeout", stage = "search", message = ex.Message });
        }
        catch (VisualSearchConfigurationException ex)
        {
            return BadRequest(new { status = "error", stage = "configuration", message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Visual search remote request failed for query {Query}", request.Query);
            return StatusCode(503, new { status = "error", stage = ex.Message.Contains("Qdrant", StringComparison.OrdinalIgnoreCase) ? "qdrant" : "cloud", message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Visual search provider returned invalid data for query {Query}", request.Query);
            return StatusCode(502, new { status = "error", stage = "cloud", message = ex.Message });
        }
        catch (Exception ex)
        {
            // Internal defects (including disposed JSON backing documents) are
            // server errors, never user configuration errors. Keep the full
            // exception and stack trace in Jellyfin logs for diagnosis.
            _logger.LogError(ex, "Visual search failed for query {Query}", request.Query);
            return StatusCode(500, new { status = "error", stage = "search", message = ex.Message });
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
        catch (VisualSearchConfigurationException ex)
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
        var libraryId = string.IsNullOrWhiteSpace(request.LibraryId)
            ? _libraryManager.GetCollectionFolders(video).FirstOrDefault()?.Id.ToString() ?? string.Empty
            : request.LibraryId;
        try
        {
            var indexed = await _indexer.IndexAsync(video, libraryId, cancellationToken).ConfigureAwait(false);
            _state.IndexedVideos++;
            _state.Status = "ready";
            return Ok(new { itemId = video.Id, textIndexed = indexed.Text, framesIndexed = indexed.Frames });
        }
        catch (TaskCanceledException ex) { return StatusCode(504, new { status = "timeout", stage = "index", message = ex.Message }); }
        catch (VisualSearchConfigurationException ex) { return BadRequest(new { status = "error", stage = "configuration", message = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(503, new { status = "error", stage = "embedding", message = ex.Message }); }
    }

    [HttpPost("Test/Random")]
    public async Task<ActionResult> TestRandom([FromBody] RandomTestRequest? request, CancellationToken cancellationToken)
    {
        var count = Math.Clamp(request?.Count ?? 3, 1, 10);
        var frames = Math.Clamp(request?.FramesPerVideo ?? 2, 1, 100);
        WorkerHealth cloud;
        try
        {
            cloud = await _client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(ex is TaskCanceledException ? 504 : 503, new { status = "error", stage = "health", message = ex.Message });
        }
        catch (VisualSearchHealthException ex)
        {
            return StatusCode(ex.StatusCode, new { status = "error", stage = ex.Stage, message = ex.Message });
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            return BadRequest(new { status = "error", stage = "configuration", message = ex.Message });
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
        var frames = Math.Clamp(request?.FramesPerVideo ?? 3, 1, 100);
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
        var frames = Math.Clamp(request?.FramesPerVideo ?? 3, 1, 100);
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

    private static IReadOnlyList<ScoredSearchHit> NormalizeHits(IReadOnlyList<RemoteSearchHit> hits)
    {
        if (hits.Count == 0) return Array.Empty<ScoredSearchHit>();
        var ordered = hits.Select(x => x.Score).OrderBy(x => x).ToArray();
        var low = Percentile(ordered, 0.10);
        var high = Percentile(ordered, 0.90);
        if (high - low < 1e-9)
        {
            low = ordered[0];
            high = ordered[^1];
        }
        return hits.Select((hit, index) =>
        {
            var normalized = high - low < 1e-9
                ? hits.Count == 1 ? 1d : 0.5d
                : Math.Clamp((hit.Score - low) / (high - low), 0, 1);
            return new ScoredSearchHit(hit, normalized);
        }).ToArray();
    }

    private static double CalculateFinalScore(SearchPresetDefinition preset, double? visualScore, double? titleScore)
    {
        var hasVisual = visualScore.HasValue;
        var hasTitle = titleScore.HasValue;
        if (string.Equals(preset.Mode, "title", StringComparison.OrdinalIgnoreCase)) return titleScore ?? 0;
        if (string.Equals(preset.Mode, "visual", StringComparison.OrdinalIgnoreCase)) return visualScore ?? 0;
        if (!hasVisual && !hasTitle) return 0;

        var visualWeight = Math.Max(0, preset.VisualWeight);
        var titleWeight = Math.Max(0, preset.TitleWeight);
        var availableWeight = (hasVisual ? visualWeight : 0) + (hasTitle ? titleWeight : 0);
        var weighted = availableWeight <= 0
            ? (visualScore ?? titleScore ?? 0)
            : ((visualScore ?? 0) * visualWeight + (titleScore ?? 0) * titleWeight) / availableWeight;

        // Only the built-in balanced preset (or an explicitly configured custom
        // preset) opts into the missing-modality penalty. Title-only, visual-only,
        // and priority presets intentionally keep their own semantics.
        if (preset.ApplyMissingModalityPenalty && hasVisual != hasTitle)
        {
            weighted *= Math.Clamp(preset.MissingModalityPenalty, 0, 1);
        }
        return Math.Clamp(weighted, 0, 1);
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return 0;
        var position = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return values[lower];
        var fraction = position - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }

    private static double AggregateVisualScore(IReadOnlyList<ScoredSearchHit> frames)
    {
        var top = frames.Take(5).Select(x => x.NormalizedScore).ToArray();
        var topOne = top[0];
        var topThree = top.Take(3).Average();
        var topFive = top.Average();
        // The best frame carries the strongest signal, while the smaller averages
        // prevent a single accidental match from dominating the video score.
        return 0.50 * topOne + 0.30 * topThree + 0.20 * topFive;
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }
}

internal sealed record ScoredSearchHit(RemoteSearchHit Hit, double NormalizedScore);
public sealed record SearchRequest(string Query, string[]? LibraryIds = null, int Limit = 0, string? PresetId = null);
public sealed record IndexItemRequest(string ItemId, string? LibraryId = null);
public sealed record RandomTestRequest(int Count = 3, int FramesPerVideo = 2);
public sealed record InspectRequest(int Count = 3, int FramesPerVideo = 3, string[]? Queries = null, string[]? ItemIds = null);
public sealed record PreviewRequest(string? ItemId = null, int FramesPerVideo = 3);
public sealed record SearchResponse(string Query, SearchResult[] Results, long ElapsedMs)
{
    public string PresetId { get; init; } = "balanced";
    public string PresetName { get; init; } = "综合搜索";
    public int ResultLimit { get; init; }
}
public sealed record SearchResult(string ItemId, string Title, double Score, double? VisualScore, double? TitleScore, BestFrame? BestFrame)
{
    public long? RunTimeTicks { get; init; }
    public string Type { get; init; } = "Video";
    public bool HasVisualMatch { get; init; }
    public bool HasTitleMatch { get; init; }
    public string MatchKind { get; init; } = "both";
}
public sealed record BestFrame(int FrameIndex, long TimestampMs, double Score);
