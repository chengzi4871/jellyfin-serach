using System.Linq;
using Jellyfin.Plugin.VisualSearch;
using Xunit;

namespace Jellyfin.Plugin.VisualSearch.Tests;

public sealed class SearchPresetTests
{
    [Fact]
    public void BuiltInBalancedPresetUsesFixedNormalSearchRules()
    {
        var configuration = new PluginConfiguration();
        var preset = SearchPresets.Resolve(configuration, "balanced");

        Assert.Equal("balanced", preset.Id);
        Assert.True(preset.ApplyMissingModalityPenalty);
        Assert.Equal(0.75, preset.VisualWeight, precision: 4);
        Assert.Equal(0.25, preset.TitleWeight, precision: 4);
        Assert.Equal(1, preset.CoverScoreMultiplier, precision: 4);
    }

    [Fact]
    public void CustomJsonPresetIsVisibleAndDoesNotInheritBalancedPenalty()
    {
        var configuration = new PluginConfiguration
        {
            SearchVisiblePresetIds = "family-recent",
            SearchCustomPresetsJson = "[{\"id\":\"family-recent\",\"name\":\"家庭视频优先\",\"mode\":\"weighted\",\"visualWeight\":0.85,\"titleWeight\":0.15,\"applyMissingModalityPenalty\":false,\"coverScoreMultiplier\":0.65,\"sortBy\":\"visual\"}]"
        };

        var presets = SearchPresets.GetVisible(configuration);
        var preset = Assert.Single(presets);
        Assert.Equal("family-recent", preset.Id);
        Assert.Equal(0.85, preset.VisualWeight, precision: 4);
        Assert.False(preset.ApplyMissingModalityPenalty);
        Assert.Equal(0.65, preset.CoverScoreMultiplier, precision: 4);
        Assert.Equal("visual", preset.SortBy);
    }
}
