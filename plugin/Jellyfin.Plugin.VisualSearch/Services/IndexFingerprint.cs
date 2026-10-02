using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.VisualSearch;

/// <summary>
/// Stable content signatures used by incremental indexing. These deliberately
/// avoid <see cref="string.GetHashCode()"/>, whose value is randomized between
/// .NET processes and therefore cannot be persisted in Qdrant.
/// </summary>
public static class IndexFingerprint
{
    public const int Version = 2;

    public static string BuildTitleText(Video video)
        => $"Title: {video.Name}\nOriginal title: {video.OriginalTitle}\nFilename: {Path.GetFileName(video.Path)}";

    public static string ComputeTextHash(Video video, PluginConfiguration configuration)
        => Hash(string.Join("\n", "text", Version, BuildTitleText(video), ProviderIdentity(configuration)));

    public static string ComputeConfigurationHash(PluginConfiguration configuration)
        => Hash(string.Join("\n", "configuration", Version, ProviderIdentity(configuration),
            configuration.GetEffectiveMaxFramesPerVideo(), configuration.GetEffectiveSceneProbeFrames(),
            configuration.FrameDeduplicationEnabled, configuration.SceneChangeSamplingEnabled,
            configuration.CoverFallbackEnabled));

    public static string ComputeVisualHash(
        Video video,
        PluginConfiguration configuration,
        IReadOnlyDictionary<string, Dictionary<int, TrickplayInfo>> manifest)
    {
        var now = DateTime.UtcNow;
        var builder = new StringBuilder();
        builder.Append("visual\n").Append(Version).Append('\n')
            .Append(video.Id).Append('\n')
            .Append(video.Path).Append('\n')
            .Append(video.Size?.ToString() ?? string.Empty).Append('\n')
            .Append(video.RunTimeTicks?.ToString() ?? string.Empty).Append('\n')
            .Append(FileSignature(video.Path)).Append('\n')
            .Append(SaneDateSignature(video.DateModified, now)).Append('\n');

        var image = video.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0);
        if (image is not null)
        {
            builder.Append("primary\n").Append(image.Path).Append('\n')
                .Append(FileSignature(image.Path)).Append('\n')
                .Append(SaneDateSignature(image.DateModified, now)).Append('\n');
        }

        foreach (var source in manifest.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var info in source.Value.OrderBy(x => x.Key))
            {
                var value = info.Value;
                builder.Append("trickplay\n").Append(source.Key).Append('\n')
                    .Append(info.Key).Append('|').Append(value.Width).Append('|').Append(value.Height).Append('|')
                    .Append(value.TileWidth).Append('|').Append(value.TileHeight).Append('|')
                    .Append(value.ThumbnailCount).Append('|').Append(value.Interval).Append('|').Append(value.Bandwidth).Append('\n');
            }
        }

        builder.Append("sampling\n")
            .Append(configuration.GetEffectiveMaxFramesPerVideo()).Append('|')
            .Append(configuration.GetEffectiveSceneProbeFrames()).Append('|')
            .Append(configuration.FrameDeduplicationEnabled).Append('|')
            .Append(configuration.SceneChangeSamplingEnabled).Append('|')
            .Append(configuration.CoverFallbackEnabled).Append('\n')
            .Append(ProviderIdentity(configuration));
        return Hash(builder.ToString());
    }

    public static bool IsPlausibleDateModified(DateTime value, DateTime referenceUtc)
    {
        var utc = NormalizeUtc(value);
        return utc >= DateTime.UnixEpoch && utc <= referenceUtc.AddDays(1);
    }

    public static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string ProviderIdentity(PluginConfiguration configuration)
        => string.Join('|', configuration.EmbeddingBaseUrl.Trim(), configuration.EmbeddingModel.Trim(),
            configuration.EmbeddingProtocol.Trim(), configuration.EmbeddingInputShape.Trim(), configuration.EmbeddingDimension);

    private static string SaneDateSignature(DateTime value, DateTime referenceUtc)
        => IsPlausibleDateModified(value, referenceUtc) ? NormalizeUtc(value).Ticks.ToString() : "invalid-date";

    private static string FileSignature(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (IOException)
        {
            return "unavailable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unavailable";
        }
        catch (ArgumentException)
        {
            return "unavailable";
        }
        catch (NotSupportedException)
        {
            return "unavailable";
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
