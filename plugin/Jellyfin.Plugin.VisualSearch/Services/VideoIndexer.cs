using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using Jellyfin.Database.Implementations.Entities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class VideoIndexer
{
    private readonly VisualSearchClient _client;
    private readonly ITrickplayManager _trickplay;
    private readonly ILibraryManager _library;
    private readonly VisualSearchState _state;

    public VideoIndexer(VisualSearchClient client, ITrickplayManager trickplay, ILibraryManager library, VisualSearchState state)
    {
        _client = client;
        _trickplay = trickplay;
        _library = library;
        _state = state;
    }

    public async Task<IndexResult> IndexAsync(Video video, string libraryId, CancellationToken cancellationToken, bool forceRebuild = false)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var text = IndexFingerprint.BuildTitleText(video);
        // Read the manifest before comparing signatures. This detects a new
        // Trickplay set even when Jellyfin's media DateModified did not change.
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        var textHash = IndexFingerprint.ComputeTextHash(video, configuration);
        var visualHash = IndexFingerprint.ComputeVisualHash(video, configuration, manifest);
        var existing = forceRebuild ? null : await _client.GetIndexedSignatureAsync(video.Id.ToString(), cancellationToken).ConfigureAwait(false);
        var textChanged = forceRebuild || existing?.TextHash != textHash;
        var visualChanged = forceRebuild || existing?.VisualHash != visualHash;
        if (!textChanged && !visualChanged)
        {
            _state.SetStage("skipped_unchanged", 0, 0, 0);
            return new IndexResult(false, 0, false, true, true);
        }

        if (textChanged)
        {
            _state.SetStage("embedding_title", 0, 1, 1);
            _state.PlanEmbeddingInputs(1);
            var titleVector = await _client.EmbedTextAsync(text, cancellationToken).ConfigureAwait(false);
            _state.RecordEmbeddingInputs(1);
            _state.SetStage("saving_title", 1, 1, 1);
            // Do not publish a new visualHash until the visual part has
            // completed. If cancellation/error occurs, the next run retries
            // only that visual part instead of falsely considering it done.
            await _client.UpsertAsync("jellyfin_video_text", new[]
            {
                new
                {
                    id = video.Id.ToString(),
                    vector = titleVector,
                    payload = BuildTextPayload(video, libraryId, textHash, visualChanged ? existing?.VisualHash : visualHash)
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        if (!visualChanged)
            return new IndexResult(textChanged, 0, false, false, true);

        _state.SetStage("reading_frames", 0, 0, 0);
        var frameSamples = new List<(FrameSample Sample, int Interval)>();
        var frameFailureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var read = await ReadDistinctFramesAsync(video, info, configuration.GetEffectiveMaxFramesPerVideo(), cancellationToken).ConfigureAwait(false);
            foreach (var reason in read.FailureReasons) frameFailureReasons[reason.Key] = frameFailureReasons.TryGetValue(reason.Key, out var old) ? old + reason.Value : reason.Value;
            _state.CurrentFramesSampled = read.SampledCount;
            _state.CurrentFramesSceneDiscarded = read.SceneDiscardedCount;
            _state.CurrentFramesDeduplicated = read.DeduplicatedCount;
            frameSamples.AddRange(read.Samples.Select(sample => (sample, info.Interval)));
            // Match Preview/Inspect: one media source, one per-video frame cap.
            break;
        }

        var images = frameSamples.Select(x => x.Sample.Bytes).ToArray();
        var frames = new List<object>(images.Length);
        var totalBatches = images.Length == 0 ? 0 : (int)Math.Ceiling(images.Length / (double)Math.Clamp(configuration.EmbeddingBatchSize, 1, 256));
        _state.SetStage("embedding_frames", 0, totalBatches, images.Length);
        _state.PlanEmbeddingInputs(images.Length);
        var imageVectors = await _client.EmbedImagesAsync(images, cancellationToken, (batch, total, count) => _state.SetBatchProgress(batch, total, count)).ConfigureAwait(false);
        for (var i = 0; i < frameSamples.Count; i++)
        {
            var sample = frameSamples[i].Sample;
            frames.Add(new { id = DeterministicId(video.Id, sample.FrameIndex), vector = imageVectors[i], payload = new { itemId = video.Id.ToString(), libraryId, visualHash, hashVersion = IndexFingerprint.Version, frameIndex = sample.FrameIndex, timestampMs = (long)sample.FrameIndex * frameSamples[i].Interval } });
        }
        _state.SetStage("saving_frames", totalBatches, totalBatches, 0);
        var coverIndexed = false;
        if (frames.Count > 0)
        {
            // A video may have been indexed with its Primary cover before Trickplay
            // became available. Keep the source unambiguous once real frames exist.
            await _client.DeleteByItemAsync("jellyfin_video_covers", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
            await _client.UpsertAsync("jellyfin_video_frames", frames, cancellationToken).ConfigureAwait(false);
            // A changed sampling configuration can select different frame
            // indices. Remove only points that were not part of this successful
            // upsert, so old samples cannot inflate later search scores.
            var currentFrameIds = frameSamples
                .Select(x => DeterministicId(video.Id, x.Sample.FrameIndex))
                .ToArray();
            await _client.DeleteByItemExceptAsync("jellyfin_video_frames", video.Id.ToString(), currentFrameIds, cancellationToken).ConfigureAwait(false);
            _state.RecordFrameSuccess(frames.Count);
        }
        else
        {
            var primaryReason = frameFailureReasons.Count == 0 ? "no_trickplay_frames" : frameFailureReasons.Keys.First();
            var failureCount = Math.Max(1, frameFailureReasons.Values.Sum());
            _state.RecordFrameFailure(primaryReason, failureCount);
            foreach (var reason in frameFailureReasons.Where(x => !string.Equals(x.Key, primaryReason, StringComparison.OrdinalIgnoreCase)))
                _state.FrameFailureReasons.AddOrUpdate(reason.Key, reason.Value, (_, old) => old + reason.Value);

            if (configuration.CoverFallbackEnabled)
            {
                var cover = await ReadPrimaryCoverAsync(video, cancellationToken).ConfigureAwait(false);
                if (cover is not null)
                {
                    _state.SetStage("embedding_cover", 0, 1, 1);
                    _state.PlanEmbeddingInputs(1);
                    var coverVector = await _client.EmbedImageAsync(cover.Bytes, cancellationToken).ConfigureAwait(false);
                    _state.RecordEmbeddingInputs(1);
                    await _client.DeleteByItemAsync("jellyfin_video_frames", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
                    await _client.UpsertAsync("jellyfin_video_covers", new[]
                    {
                        new
                        {
                            id = DeterministicCoverId(video.Id),
                            vector = coverVector,
                            payload = new
                            {
                                itemId = video.Id.ToString(),
                                libraryId,
                                visualHash,
                                hashVersion = IndexFingerprint.Version,
                                visualSource = "primary_cover",
                                imageTag = cover.Tag
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);
                    _state.RecordCoverSuccess();
                    coverIndexed = true;
                }
                else
                {
                    await _client.DeleteByItemAsync("jellyfin_video_frames", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
                    await _client.DeleteByItemAsync("jellyfin_video_covers", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _client.DeleteByItemAsync("jellyfin_video_frames", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
                await _client.DeleteByItemAsync("jellyfin_video_covers", video.Id.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
        // For a visual-only change, keep the old title vector and update only
        // its payload. For a text+visual change this finalizes the payload
        // that was intentionally written with the previous visualHash above.
        await _client.SetPayloadAsync("jellyfin_video_text", video.Id.ToString(),
            BuildTextPayload(video, libraryId, textHash, visualHash), cancellationToken).ConfigureAwait(false);
        _state.SetStage("video_completed", 0, 0, 0);
        return new IndexResult(textChanged, frames.Count, coverIndexed, false, true);
    }

    public async Task<bool> IsContentCurrentAsync(Video video, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        var existing = await _client.GetIndexedSignatureAsync(video.Id.ToString(), cancellationToken).ConfigureAwait(false);
        return existing?.TextHash == IndexFingerprint.ComputeTextHash(video, configuration)
            && existing?.VisualHash == IndexFingerprint.ComputeVisualHash(video, configuration, manifest);
    }

    private static object BuildTextPayload(Video video, string libraryId, string textHash, string? visualHash)
        => new
        {
            itemId = video.Id.ToString(),
            libraryId,
            textHash,
            visualHash,
            hashVersion = IndexFingerprint.Version
        };

    private static async Task<PrimaryCoverRead?> ReadPrimaryCoverAsync(Video video, CancellationToken cancellationToken)
    {
        var image = video.GetImageInfo(ImageType.Primary, 0);
        if (image is null || !image.IsLocalFile || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(image.Path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            using (Image.Load<Rgba32>(bytes))
            {
                return new PrimaryCoverRead(bytes, image.DateModified.ToString("O"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string BuildTitleText(Video video) => IndexFingerprint.BuildTitleText(video);

    public async Task<EmbeddingProbeResult> ProbeAsync(Video video, int maxFrames, CancellationToken cancellationToken)
    {
        var text = BuildTitleText(video);
        await _client.EmbedTextAsync(text, cancellationToken).ConfigureAwait(false);
        var framesTested = 0;
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = (await ReadDistinctFramesAsync(video, info, Math.Max(1, maxFrames), cancellationToken).ConfigureAwait(false)).Samples;
            await _client.EmbedImagesAsync(samples.Select(x => x.Bytes).ToArray(), cancellationToken).ConfigureAwait(false);
            framesTested = samples.Count;
            break;
        }
        return new EmbeddingProbeResult(video.Id.ToString(), video.Name, true, framesTested, null);
    }

    public async Task<DetailedProbeResult> ProbeDetailedAsync(Video video, int maxFrames, string[] queries, CancellationToken cancellationToken)
    {
        var titleText = BuildTitleText(video);
        var titleVector = await _client.EmbedTextAsync(titleText, cancellationToken).ConfigureAwait(false);
        var queryList = queries.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(20).ToArray();
        var queryVectors = new Dictionary<string, float[]>();
        var vectors = await _client.EmbedTextsAsync(queryList, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < queryList.Length; i++) queryVectors[queryList[i]] = vectors[i];

        var frames = new List<ProbeFrame>();
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = (await ReadDistinctFramesAsync(video, info, Math.Max(1, maxFrames), cancellationToken).ConfigureAwait(false)).Samples;
            var imageVectors = await _client.EmbedImagesAsync(samples.Select(x => x.Bytes).ToArray(), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < samples.Count; i++)
            {
                var frame = samples[i].FrameIndex;
                var vector = imageVectors[i];
                var scores = queryVectors.ToDictionary(x => x.Key, x => Cosine(x.Value, vector));
                frames.Add(new ProbeFrame(frame, (long)frame * info.Interval, $"data:image/jpeg;base64,{Convert.ToBase64String(samples[i].Bytes)}", VectorSummary.From(vector), scores));
            }
            break;
        }
        var titleScores = queryVectors.ToDictionary(x => x.Key, x => Cosine(x.Value, titleVector));
        return new DetailedProbeResult(video.Id.ToString(), video.Name, titleText, VectorSummary.From(titleVector), titleScores, frames, queryVectors.Keys.ToArray());
    }

    public async Task<ProbePreviewResult> PreviewAsync(Video video, int maxFrames, CancellationToken cancellationToken)
    {
        var titleText = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        var previewFrames = new List<ProbePreviewFrame>();
        var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var read = await ReadDistinctFramesAsync(video, info, Math.Max(1, maxFrames), cancellationToken).ConfigureAwait(false);
            var samples = read.Samples;
            foreach (var reason in read.FailureReasons) reasons[reason.Key] = reasons.TryGetValue(reason.Key, out var old) ? old + reason.Value : reason.Value;
            previewFrames.AddRange(samples.Select(sample => new ProbePreviewFrame(sample.FrameIndex, (long)sample.FrameIndex * info.Interval, $"data:image/jpeg;base64,{Convert.ToBase64String(sample.Bytes)}")));
            break;
        }
        var reasonText = previewFrames.Count > 0 ? null : reasons.Count == 0 ? "该视频没有 Trickplay 瓦片，请先生成或更换视频" : string.Join("；", reasons.Select(x => $"{x.Key}: {x.Value}"));
        return new ProbePreviewResult(video.Id.ToString(), video.Name, titleText, previewFrames, reasonText);
    }

    private static async Task<Image<Rgba32>> LoadTileAsync(string tilePath, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(tilePath);
        return await Image.LoadAsync<Rgba32>(input, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Image<Rgba32> tile, int frame, TrickplayInfo info, CancellationToken cancellationToken)
    {
        var column = frame % info.TileWidth;
        var row = (frame / info.TileWidth) % info.TileHeight;
        var x = column * info.Width;
        var y = row * info.Height;
        if (x >= tile.Width || y >= tile.Height) throw new InvalidDataException("Trickplay frame is outside tile bounds");
        var width = Math.Min(info.Width, tile.Width - x);
        var height = Math.Min(info.Height, tile.Height - y);
        using var image = tile.Clone(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 85 }, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task<FrameReadResult> ReadDistinctFramesAsync(Video video, TrickplayInfo info, int maxFrames, CancellationToken cancellationToken)
    {
        var estimatedSeconds = Math.Max(1d, info.ThumbnailCount * info.Interval / 1000d);
        var target = (int)Math.Ceiling(estimatedSeconds / 30d);
        var cap = Math.Clamp(maxFrames, 1, 100);
        var sampleCount = Math.Min(info.ThumbnailCount, Math.Min(cap, Math.Max(6, target)));
        var sceneSampling = Plugin.Instance?.Configuration.SceneChangeSamplingEnabled != false
            && cap >= 8 && estimatedSeconds >= 300;
        // Long videos get a configurable sparse probe pass (default 72). The
        // extra probes are discarded locally; only the selected frames reach Embedding.
        var probeCount = sceneSampling
            ? Math.Min(info.ThumbnailCount, Math.Max(sampleCount, Plugin.Instance?.Configuration.GetEffectiveSceneProbeFrames() ?? Math.Max(cap * 3, 72)))
            : sampleCount;
        var indices = Enumerable.Range(0, probeCount).Select(i => probeCount == 1 ? 0 : (int)Math.Round(i * (info.ThumbnailCount - 1d) / (probeCount - 1))).Distinct().ToList();
        var kept = new List<FrameSample>();
        var candidates = new List<FrameCandidate>();
        var deduplicatedCount = 0;
        var deduplicate = Plugin.Instance?.Configuration.FrameDeduplicationEnabled != false;
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var saveWithMedia = _library.GetLibraryOptions(video).SaveTrickplayWithMedia;
        var tileCache = new Dictionary<int, Image<Rgba32>>();
        try
        {
            foreach (var frame in indices)
            {
                var tileIndex = frame / (info.TileWidth * info.TileHeight);
                var path = await FindTilePathAsync(video, info.Width, tileIndex, saveWithMedia).ConfigureAwait(false);
                if (path is null)
                {
                    failures["tile_missing"] = failures.TryGetValue("tile_missing", out var missing) ? missing + 1 : 1;
                    continue;
                }
                try
                {
                    if (!tileCache.TryGetValue(tileIndex, out var tile))
                    {
                        tile = await LoadTileAsync(path, cancellationToken).ConfigureAwait(false);
                        tileCache[tileIndex] = tile;
                    }
                    var bytes = await ReadFrameAsync(tile, frame, info, cancellationToken).ConfigureAwait(false);
                    candidates.Add(new FrameCandidate(frame, bytes, Fingerprint(bytes), 0));
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    failures["tile_read_error"] = failures.TryGetValue("tile_read_error", out var readError) ? readError + 1 : 1;
                }
            }
        }
        finally
        {
            foreach (var tile in tileCache.Values) tile.Dispose();
        }

        var selected = SelectSceneCandidates(candidates, cap);
        var sceneDiscardedCount = Math.Max(0, candidates.Count - selected.Count);
        var fingerprints = new List<FrameFingerprint>();
        foreach (var candidate in selected)
        {
            if (deduplicate && candidate.FrameIndex != selected[0].FrameIndex && candidate.FrameIndex != selected[^1].FrameIndex && fingerprints.Any(previous => IsNearDuplicate(previous, candidate.Fingerprint)))
            {
                deduplicatedCount++;
                continue;
            }
            fingerprints.Add(candidate.Fingerprint);
            kept.Add(new FrameSample(candidate.FrameIndex, candidate.Bytes));
        }
        return new FrameReadResult(kept, failures, indices.Count, deduplicatedCount, sceneDiscardedCount);
    }

    private static IReadOnlyList<FrameCandidate> SelectSceneCandidates(IReadOnlyList<FrameCandidate> candidates, int maxFrames)
    {
        if (candidates.Count <= maxFrames) return candidates.OrderBy(x => x.FrameIndex).ToArray();
        var ordered = candidates.OrderBy(x => x.FrameIndex).ToArray();
        foreach (var candidate in ordered)
        {
            var position = Array.IndexOf(ordered, candidate);
            var previous = position > 0 ? ordered[position - 1].Fingerprint : candidate.Fingerprint;
            var next = position + 1 < ordered.Length ? ordered[position + 1].Fingerprint : candidate.Fingerprint;
            candidate.SceneScore = Math.Max(SceneDifference(previous, candidate.Fingerprint), SceneDifference(candidate.Fingerprint, next));
        }
        var selected = new List<FrameCandidate> { ordered[0] };
        if (maxFrames > 1) selected.Add(ordered[^1]);
        while (selected.Count < maxFrames)
        {
            var next = ordered.Where(candidate => !selected.Contains(candidate))
                .OrderByDescending(candidate => candidate.SceneScore * 0.7 + TemporalGap(candidate, selected) * 0.3)
                .FirstOrDefault();
            if (next is null) break;
            selected.Add(next);
        }
        return selected.OrderBy(x => x.FrameIndex).ToArray();
    }

    private static double TemporalGap(FrameCandidate candidate, IReadOnlyList<FrameCandidate> selected)
    {
        var nearest = selected.Min(x => Math.Abs(x.FrameIndex - candidate.FrameIndex));
        return nearest / (double)Math.Max(1, candidate.FrameIndex + 1);
    }

    private static double SceneDifference(FrameFingerprint first, FrameFingerprint second)
    {
        var hashDifference = System.Numerics.BitOperations.PopCount(first.Hash ^ second.Hash) / 64d;
        var colorDifference = first.Colors.Zip(second.Colors, (a, b) => Math.Abs(a - b)).Average() / 255d;
        return 0.55 * hashDifference + 0.45 * colorDifference;
    }

    private async Task<string?> FindTilePathAsync(Video video, int width, int tileIndex, bool preferredSaveWithMedia)
    {
        foreach (var saveWithMedia in new[] { preferredSaveWithMedia, !preferredSaveWithMedia }.Distinct())
        {
            var path = await _trickplay.GetTrickplayTilePathAsync(video, width, tileIndex, saveWithMedia).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        }
        return null;
    }

    // Decode each sampled JPEG once; comparisons use only 64-bit hashes and 8x8 colors.
    private static FrameFingerprint Fingerprint(byte[] bytes)
    {
        using var image = Image.Load<Rgba32>(bytes);
        image.Mutate(x => x.Resize(8, 8));
        var colors = new byte[8 * 8 * 3];
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
        {
            var offset = (y * 8 + x) * 3;
            colors[offset] = image[x, y].R;
            colors[offset + 1] = image[x, y].G;
            colors[offset + 2] = image[x, y].B;
        }
        image.Mutate(x => x.Resize(9, 8).Grayscale());
        ulong hash = 0;
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
            if (image[x, y].R > image[x + 1, y].R) hash |= 1UL << (y * 8 + x);
        return new FrameFingerprint(hash, colors);
    }

    private static bool IsNearDuplicate(FrameFingerprint first, FrameFingerprint second)
    {
        if (System.Numerics.BitOperations.PopCount(first.Hash ^ second.Hash) > 5) return false;
        var colorDistance = first.Colors.Zip(second.Colors, (a, b) => Math.Abs(a - b)).Average();
        return colorDistance <= 18;
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Count; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa == 0 || bb == 0 ? 0 : dot / (Math.Sqrt(aa) * Math.Sqrt(bb));
    }

    private static string DeterministicId(Guid itemId, int frame)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{itemId:N}:{frame}"))).ToString();
    }

    private static string DeterministicCoverId(Guid itemId)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{itemId:N}:primary-cover"))).ToString();
    }
}

internal sealed record FrameSample(int FrameIndex, byte[] Bytes);
internal sealed record FrameFingerprint(ulong Hash, byte[] Colors);
internal sealed class FrameCandidate
{
    public FrameCandidate(int frameIndex, byte[] bytes, FrameFingerprint fingerprint, double sceneScore)
    {
        FrameIndex = frameIndex;
        Bytes = bytes;
        Fingerprint = fingerprint;
        SceneScore = sceneScore;
    }

    public int FrameIndex { get; }
    public byte[] Bytes { get; }
    public FrameFingerprint Fingerprint { get; }
    public double SceneScore { get; set; }
}
internal sealed record FrameReadResult(IReadOnlyList<FrameSample> Samples, IReadOnlyDictionary<string, int> FailureReasons, int SampledCount, int DeduplicatedCount, int SceneDiscardedCount);
internal sealed record PrimaryCoverRead(byte[] Bytes, string Tag);

public sealed record EmbeddingProbeResult(string ItemId, string Title, bool TextAccepted, int FramesTested, string? Error);
public sealed record IndexResult(bool Text, int Frames, bool Cover, bool Skipped, bool Success);
public sealed record DetailedProbeResult(string ItemId, string Title, string TitleInput, VectorSummary TitleVector, IReadOnlyDictionary<string, double> TitleQueryScores, IReadOnlyList<ProbeFrame> Frames, IReadOnlyList<string> Queries);
public sealed record ProbePreviewResult(string ItemId, string Title, string TitleInput, IReadOnlyList<ProbePreviewFrame> Frames, string? Reason);
public sealed record ProbePreviewFrame(int FrameIndex, long TimestampMs, string ImageDataUrl);
public sealed record ProbeFrame(int FrameIndex, long TimestampMs, string ImageDataUrl, VectorSummary Vector, IReadOnlyDictionary<string, double> QueryScores);
public sealed record VectorSummary(int Dimension, double Norm, double Min, double Max, double Mean, IReadOnlyList<double> FirstValues)
{
    public static VectorSummary From(IReadOnlyList<float> vector)
    {
        if (vector.Count == 0) return new(0, 0, 0, 0, 0, Array.Empty<double>());
        var min = vector.Min(); var max = vector.Max(); var mean = vector.Average(x => (double)x);
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        return new(vector.Count, norm, min, max, mean, vector.Take(8).Select(x => (double)x).ToArray());
    }
}
