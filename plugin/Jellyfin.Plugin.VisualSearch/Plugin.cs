using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer) => Instance = this;

    public static Plugin? Instance { get; private set; }
    public override string Name => "Visual Search";
    public override string Description => "Video-level multimodal semantic search using titles, Trickplay frames and primary covers.";
    public override Guid Id => Guid.Parse("5e1d5d27-2a38-4b4f-9d5e-0dba1e3b4b18");

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "VisualSearch",
            EmbeddedResourcePath = "Jellyfin.Plugin.VisualSearch.Web.configPage.html"
        }
    };
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    // Keep this empty by default: 127.0.0.1 points at the Jellyfin container in Docker.
    public string QdrantUrl { get; set; } = "";
    public string EmbeddingBaseUrl { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public string EmbeddingApiKey { get; set; } = "";
    public string EmbeddingProtocol { get; set; } = "openai_multimodal";
    /// <summary>auto uses provider-compatible {text}/{image} input and falls back to scalar input on HTTP 400.</summary>
    public string EmbeddingInputShape { get; set; } = "auto";
    public int EmbeddingDimension { get; set; } = 1024;
    public int EmbeddingTimeoutSeconds { get; set; } = 60;
    /// <summary>Maximum number of text or image inputs sent in one Embedding request.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;
    public int EmbeddingMaxImageDimension { get; set; } = 768;
    /// <summary>Maximum screenshots per video; the sampler may choose fewer for short videos.</summary>
    public int MaxFramesPerVideo { get; set; }
    /// <summary>Legacy setting retained so older XML configurations continue to load.</summary>
    public int FramesPerVideo { get; set; } = 12;
    public bool FrameDeduplicationEnabled { get; set; } = true;
    /// <summary>Use a small sparse probe pass to prefer scene boundaries on long videos.</summary>
    public bool SceneChangeSamplingEnabled { get; set; } = true;
    /// <summary>Use Jellyfin's Primary image as the visual fallback when no usable Trickplay frame exists.</summary>
    public bool CoverFallbackEnabled { get; set; } = true;
    /// <summary>Maximum number of cards returned to the semantic results page.</summary>
    public int SearchResultLimit { get; set; } = 30;
    /// <summary>Built-in or custom preset id selected when the search panel opens.</summary>
    public string DefaultSearchPresetId { get; set; } = "balanced";
    /// <summary>Comma separated ids exposed in the search panel. Empty means all built-ins and custom definitions.</summary>
    public string SearchVisiblePresetIds { get; set; } = "balanced,title,visual,visual-priority,title-priority,custom";
    /// <summary>JSON array of additional preset definitions. This is intentionally text based so Jellyfin's XML configuration remains stable.</summary>
    public string SearchCustomPresetsJson { get; set; } = "";
    /// <summary>JavaScript hook documented in the configuration page. It is applied by the result page to the raw candidate list.</summary>
    public string SearchCustomCode { get; set; } = "return items.map(function (item) { item.customScore = item.score; return item; }).sort(function (a, b) { return b.customScore - a.customScore; });";
    public bool SearchShowBestFramePoster { get; set; } = true;
    public bool SearchShowBestFrameTimestamp { get; set; } = true;
    public bool SearchShowScoreBreakdown { get; set; } = true;
    public bool SearchShowRank { get; set; } = true;
    public bool SearchShowPlayAll { get; set; } = true;
    public bool SearchShowQueueAction { get; set; } = true;
    public bool ScheduledIndexEnabled { get; set; } = false;
    /// <summary>One line per weekday, e.g. 周一 02:00-06:00;22:00-23:30.</summary>
    public string ScheduledIndexWindows { get; set; } = string.Empty;
    public int IndexRetryDelaySeconds { get; set; } = 15;

    public int GetEffectiveMaxFramesPerVideo() => Math.Clamp(MaxFramesPerVideo > 0 ? MaxFramesPerVideo : FramesPerVideo, 1, 100);
}
