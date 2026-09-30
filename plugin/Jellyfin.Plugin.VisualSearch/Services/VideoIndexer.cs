using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
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

    public async Task<(bool Text, int Frames)> IndexAsync(Video video, string libraryId, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var text = BuildTitleText(video);
        _state.SetStage("embedding_title", 0, 1, 1);
        _state.PlanEmbeddingInputs(1);
        var titleVector = await _client.EmbedTextAsync(text, cancellationToken).ConfigureAwait(false);
        _state.RecordEmbeddingInputs(1);
        _state.SetStage("saving_title", 1, 1, 1);
        await _client.UpsertAsync("jellyfin_video_text", new[]
        {
            new { id = video.Id.ToString(), vector = titleVector, payload = new { itemId = video.Id.ToString(), libraryId, textHash = text.GetHashCode().ToString() } }
        }, cancellationToken).ConfigureAwait(false);

        _state.SetStage("reading_frames", 0, 0, 0);
        var frameSamples = new List<(FrameSample Sample, int Interval)>();
        var frameFailureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var read = await ReadDistinctFramesAsync(video, info, configuration.GetEffectiveMaxFramesPerVideo(), cancellationToken).ConfigureAwait(false);
            foreach (var reason in read.FailureReasons) frameFailureReasons[reason.Key] = frameFailureReasons.TryGetValue(reason.Key, out var old) ? old + reason.Value : reason.Value;
            _state.CurrentFramesSampled = read.SampledCount;
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
            frames.Add(new { id = DeterministicId(video.Id, sample.FrameIndex), vector = imageVectors[i], payload = new { itemId = video.Id.ToString(), libraryId, frameIndex = sample.FrameIndex, timestampMs = (long)sample.FrameIndex * frameSamples[i].Interval } });
        }
        _state.SetStage("saving_frames", totalBatches, totalBatches, 0);
        if (frames.Count > 0) await _client.UpsertAsync("jellyfin_video_frames", frames, cancellationToken).ConfigureAwait(false);
        if (frames.Count > 0) _state.RecordFrameSuccess(frames.Count);
        else
        {
            var primaryReason = frameFailureReasons.Count == 0 ? "no_trickplay_frames" : frameFailureReasons.Keys.First();
            var failureCount = Math.Max(1, frameFailureReasons.Values.Sum());
            _state.RecordFrameFailure(primaryReason, failureCount);
            foreach (var reason in frameFailureReasons.Where(x => !string.Equals(x.Key, primaryReason, StringComparison.OrdinalIgnoreCase)))
                _state.FrameFailureReasons.AddOrUpdate(reason.Key, reason.Value, (_, old) => old + reason.Value);
        }
        _state.SetStage("video_completed", 0, 0, 0);
        return (true, frames.Count);
    }

    private static string BuildTitleText(Video video)
        => $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";

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

    private static async Task<byte[]> ReadFrameAsync(string tilePath, int frame, TrickplayInfo info, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(tilePath);
        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        var column = frame % info.TileWidth;
        var row = (frame / info.TileWidth) % info.TileHeight;
        var x = column * info.Width;
        var y = row * info.Height;
        if (x >= image.Width || y >= image.Height) throw new InvalidDataException("Trickplay frame is outside tile bounds");
        var width = Math.Min(info.Width, image.Width - x);
        var height = Math.Min(info.Height, image.Height - y);
        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 85 }, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task<FrameReadResult> ReadDistinctFramesAsync(Video video, TrickplayInfo info, int maxFrames, CancellationToken cancellationToken)
    {
        var estimatedSeconds = Math.Max(1d, info.ThumbnailCount * info.Interval / 1000d);
        var target = (int)Math.Ceiling(estimatedSeconds / 30d);
        var sampleCount = Math.Min(info.ThumbnailCount, Math.Min(Math.Max(1, maxFrames), Math.Max(6, target)));
        var indices = Enumerable.Range(0, sampleCount).Select(i => sampleCount == 1 ? 0 : (int)Math.Round(i * (info.ThumbnailCount - 1d) / (sampleCount - 1))).Distinct().ToList();
        var kept = new List<FrameSample>();
        var fingerprints = new List<FrameFingerprint>();
        var deduplicatedCount = 0;
        var deduplicate = Plugin.Instance?.Configuration.FrameDeduplicationEnabled != false;
        var failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var saveWithMedia = _library.GetLibraryOptions(video).SaveTrickplayWithMedia;
        foreach (var frame in indices)
        {
            var tileIndex = frame / (info.TileWidth * info.TileHeight);
            var path = await FindTilePathAsync(video, info.Width, tileIndex, saveWithMedia).ConfigureAwait(false);
            if (path is null)
            {
                failures["tile_missing"] = failures.TryGetValue("tile_missing", out var missing) ? missing + 1 : 1;
                continue;
            }
            byte[] bytes;
            try { bytes = await ReadFrameAsync(path, frame, info, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                failures["tile_read_error"] = failures.TryGetValue("tile_read_error", out var readError) ? readError + 1 : 1;
                continue;
            }
            if (deduplicate)
            {
                var fingerprint = Fingerprint(bytes);
                if (frame != indices[0] && frame != indices[^1] && fingerprints.Any(previous => IsNearDuplicate(previous, fingerprint)))
                {
                    deduplicatedCount++;
                    continue;
                }
                fingerprints.Add(fingerprint);
            }
            kept.Add(new FrameSample(frame, bytes));
        }
        return new FrameReadResult(kept, failures, indices.Count, deduplicatedCount);
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
}

internal sealed record FrameSample(int FrameIndex, byte[] Bytes);
internal sealed record FrameFingerprint(ulong Hash, byte[] Colors);
internal sealed record FrameReadResult(IReadOnlyList<FrameSample> Samples, IReadOnlyDictionary<string, int> FailureReasons, int SampledCount, int DeduplicatedCount);

public sealed record EmbeddingProbeResult(string ItemId, string Title, bool TextAccepted, int FramesTested, string? Error);
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
