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
    public override string Description => "Video-level multimodal semantic search using titles and Trickplay frames.";
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
    public string WorkerUrl { get; set; } = "http://127.0.0.1:8099";
    public string QdrantUrl { get; set; } = "http://127.0.0.1:6333";
    public int FramesPerVideo { get; set; } = 12;
    public double VisualWeight { get; set; } = 0.75;
    public double TitleWeight { get; set; } = 0.25;
}
