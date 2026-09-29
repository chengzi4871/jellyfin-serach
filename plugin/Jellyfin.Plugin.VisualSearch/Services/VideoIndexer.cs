using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class VideoIndexer
{
    private readonly VisualSearchClient _client;
    private readonly ITrickplayManager _trickplay;

    public VideoIndexer(VisualSearchClient client, ITrickplayManager trickplay)
    {
        _client = client;
        _trickplay = trickplay;
    }

    public async Task<(bool Text, int Frames)> IndexAsync(Video video, string libraryId, CancellationToken cancellationToken)
    {
        var text = $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";
        await _client.UpsertAsync("jellyfin_video_text", new[]
        {
            new { id = video.Id.ToString(), vector = await _client.EmbedTextAsync(text, cancellationToken), payload = new { itemId = video.Id.ToString(), libraryId, textHash = text.GetHashCode().ToString() } }
        }, cancellationToken).ConfigureAwait(false);

        var frames = new List<object>();
        var manifest = await _trickplay.GetTrickplayManifest(video).ConfigureAwait(false);
        foreach (var mediaSource in manifest.Values)
        {
            if (mediaSource.Count == 0) continue;
            var info = mediaSource.OrderBy(x => x.Key).First().Value;
            var samples = Math.Min(Plugin.Instance?.Configuration.FramesPerVideo ?? 12, info.ThumbnailCount);
            for (var i = 0; i < samples; i++)
            {
                var frame = samples == 1 ? 0 : (int)Math.Round(i * (info.ThumbnailCount - 1d) / (samples - 1));
                var tileIndex = frame / (info.TileWidth * info.TileHeight);
                var path = await _trickplay.GetTrickplayTilePathAsync(video, info.Width, tileIndex, false).ConfigureAwait(false);
                if (!File.Exists(path)) continue;
                var vector = await _client.EmbedImageAsync(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                frames.Add(new { id = DeterministicId(video.Id, frame), vector, payload = new { itemId = video.Id.ToString(), libraryId, frameIndex = frame, timestampMs = frame * info.Interval } });
            }
        }
        if (frames.Count > 0) await _client.UpsertAsync("jellyfin_video_frames", frames, cancellationToken).ConfigureAwait(false);
        return (true, frames.Count);
    }

    private static string DeterministicId(Guid itemId, int frame)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{itemId:N}:{frame}"))).ToString();
    }
}
