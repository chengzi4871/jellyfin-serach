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
        var text = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        await _client.UpsertAsync("jellyfin_video_text", new[]
        {
            new { id = video.Id.ToString(), vector = await _client.EmbedTextAsync(text, cancellationToken), payload = new { itemId = video.Id.ToString(), libraryId, textHash = text.GetHashCode().ToString() } }
        }, cancellationToken).ConfigureAwait(false);

        var frames = new List<object>();
        var frameFailureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var read = await ReadDistinctFramesAsync(video, info, Plugin.Instance?.Configuration.FramesPerVideo ?? 12, cancellationToken).ConfigureAwait(false);
            var samples = read.Samples;
            foreach (var reason in read.FailureReasons) frameFailureReasons[reason.Key] = frameFailureReasons.TryGetValue(reason.Key, out var old) ? old + reason.Value : reason.Value;
            foreach (var sample in samples)
            {
                var vector = await _client.EmbedImageAsync(sample.Bytes, cancellationToken).ConfigureAwait(false);
                frames.Add(new { id = DeterministicId(video.Id, sample.FrameIndex), vector, payload = new { itemId = video.Id.ToString(), libraryId, frameIndex = sample.FrameIndex, timestampMs = (long)sample.FrameIndex * info.Interval } });
            }
        }
        if (frames.Count > 0) await _client.UpsertAsync("jellyfin_video_frames", frames, cancellationToken).ConfigureAwait(false);
        if (frames.Count > 0) _state.RecordFrameSuccess(frames.Count);
        else
        {
            var primaryReason = frameFailureReasons.Count == 0 ? "no_trickplay_frames" : frameFailureReasons.Keys.First();
            var failureCount = Math.Max(1, frameFailureReasons.Values.Sum());
            _state.RecordFrameFailure(primaryReason, failureCount);
            foreach (var reason in frameFailureReasons.Where(x => !string.Equals(x.Key, primaryReason, StringComparison.OrdinalIgnoreCase)))
                _state.FailureReasons.AddOrUpdate(reason.Key, reason.Value, (_, old) => old + reason.Value);
        }
        return (true, frames.Count);
    }

    public async Task<EmbeddingProbeResult> ProbeAsync(Video video, int maxFrames, CancellationToken cancellationToken)
    {
        var text = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        await _client.EmbedTextAsync(text, cancellationToken).ConfigureAwait(false);
        var framesTested = 0;
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = (await ReadDistinctFramesAsync(video, info, Math.Max(1, maxFrames), cancellationToken).ConfigureAwait(false)).Samples;
            foreach (var sample in samples)
            {
                await _client.EmbedImageAsync(sample.Bytes, cancellationToken).ConfigureAwait(false);
                framesTested++;
            }
            break;
        }
        return new EmbeddingProbeResult(video.Id.ToString(), video.Name, true, framesTested, null);
    }

    public async Task<DetailedProbeResult> ProbeDetailedAsync(Video video, int maxFrames, string[] queries, CancellationToken cancellationToken)
    {
        var titleText = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        var titleVector = await _client.EmbedTextAsync(titleText, cancellationToken).ConfigureAwait(false);
        var queryVectors = new Dictionary<string, float[]>();
        foreach (var query in queries.Where(x => !string.IsNullOrWhiteSpace(x)).Take(20))
            queryVectors[query] = await _client.EmbedTextAsync(query.Trim(), cancellationToken).ConfigureAwait(false);

        var frames = new List<ProbeFrame>();
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = (await ReadDistinctFramesAsync(video, info, Math.Max(1, maxFrames), cancellationToken).ConfigureAwait(false)).Samples;
            foreach (var sample in samples)
            {
                var frame = sample.FrameIndex;
                var frameBytes = sample.Bytes;
                var vector = await _client.EmbedImageAsync(frameBytes, cancellationToken).ConfigureAwait(false);
                var scores = queryVectors.ToDictionary(x => x.Key, x => Cosine(x.Value, vector));
                frames.Add(new ProbeFrame(frame, (long)frame * info.Interval, $"data:image/jpeg;base64,{Convert.ToBase64String(frameBytes)}", VectorSummary.From(vector), scores));
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
            catch (Exception ex) when (ex is IOException or InvalidDataException or SixLabors.ImageSharp.UnknownImageFormatException)
            {
                failures["tile_read_error"] = failures.TryGetValue("tile_read_error", out var readError) ? readError + 1 : 1;
                continue;
            }
            if (frame != indices[0] && frame != indices[^1] && kept.Any(x => IsNearDuplicate(x.Bytes, bytes))) continue;
            kept.Add(new FrameSample(frame, bytes));
        }
        return new FrameReadResult(kept, failures);
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

    private static bool IsNearDuplicate(byte[] first, byte[] second)
    {
        using var a = Image.Load<Rgba32>(first);
        using var b = Image.Load<Rgba32>(second);
        var colorDistance = AverageColorDistance(a, b);
        if (colorDistance > 18) return false;
        a.Mutate(x => x.Resize(9, 8).Grayscale());
        b.Mutate(x => x.Resize(9, 8).Grayscale());
        ulong ha = 0, hb = 0;
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
        {
            if (a[x, y].R > a[x + 1, y].R) ha |= 1UL << (y * 8 + x);
            if (b[x, y].R > b[x + 1, y].R) hb |= 1UL << (y * 8 + x);
        }
        var distance = System.Numerics.BitOperations.PopCount(ha ^ hb);
        return distance <= 5;
    }

    private static double AverageColorDistance(Image<Rgba32> first, Image<Rgba32> second)
    {
        first.Mutate(x => x.Resize(8, 8));
        second.Mutate(x => x.Resize(8, 8));
        double total = 0;
        for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
        {
            var p = first[x, y]; var q = second[x, y];
            total += (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3d;
        }
        return total / 64d;
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
internal sealed record FrameReadResult(IReadOnlyList<FrameSample> Samples, IReadOnlyDictionary<string, int> FailureReasons);

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
