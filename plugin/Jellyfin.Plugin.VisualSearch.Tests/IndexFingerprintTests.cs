using System;
using Jellyfin.Plugin.VisualSearch;
using Xunit;

namespace Jellyfin.Plugin.VisualSearch.Tests;

public sealed class IndexFingerprintTests
{
    [Fact]
    public void FutureDateModifiedIsRejectedAsAnomalous()
    {
        var now = new DateTime(2026, 10, 2, 3, 30, 0, DateTimeKind.Utc);

        Assert.True(IndexFingerprint.IsPlausibleDateModified(now.AddMinutes(-1), now));
        Assert.False(IndexFingerprint.IsPlausibleDateModified(new DateTime(2098, 1, 1), now));
        Assert.False(IndexFingerprint.IsPlausibleDateModified(DateTime.MinValue, now));
    }

    [Fact]
    public void ConfigurationFingerprintIsStableAndChangesWhenSamplingChanges()
    {
        var first = new PluginConfiguration { EmbeddingModel = "model", EmbeddingDimension = 1024, MaxFramesPerVideo = 12 };
        var second = new PluginConfiguration { EmbeddingModel = "model", EmbeddingDimension = 1024, MaxFramesPerVideo = 12 };
        var changed = new PluginConfiguration { EmbeddingModel = "model", EmbeddingDimension = 1024, MaxFramesPerVideo = 24 };

        Assert.Equal(IndexFingerprint.ComputeConfigurationHash(first), IndexFingerprint.ComputeConfigurationHash(second));
        Assert.NotEqual(IndexFingerprint.ComputeConfigurationHash(first), IndexFingerprint.ComputeConfigurationHash(changed));
    }
}
