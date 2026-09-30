using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.VisualSearch;

/// <summary>Search presets are deliberately small, serializable descriptions. They keep the normal ranking deterministic and make custom ranking extensible without changing the vector index.</summary>
public sealed record SearchPresetDefinition(
    string Id,
    string Name,
    string Description,
    string Mode,
    double VisualWeight,
    double TitleWeight,
    bool ApplyMissingModalityPenalty,
    double MissingModalityPenalty,
    string SortBy,
    bool BuiltIn = false);

public static class SearchPresets
{
    public static readonly IReadOnlyList<SearchPresetDefinition> BuiltIns = new[]
    {
        new SearchPresetDefinition("balanced", "综合搜索", "标题与画面共同参与；缺失模态会适度降权。", "balanced", 0.75, 0.25, true, 0.65, "score", true),
        new SearchPresetDefinition("title", "只看标题", "只使用标题向量，适合文件名或标题描述明确的内容。", "title", 0, 1, false, 1, "title", true),
        new SearchPresetDefinition("visual", "只看画面", "只使用 Trickplay 画面向量，忽略标题。", "visual", 1, 0, false, 1, "visual", true),
        new SearchPresetDefinition("visual-priority", "画面优先", "画面权重高，标题作为辅助信号。", "weighted", 0.9, 0.1, false, 1, "score", true),
        new SearchPresetDefinition("title-priority", "标题优先", "标题权重高，画面作为辅助信号。", "weighted", 0.25, 0.75, false, 1, "score", true),
        new SearchPresetDefinition("custom", "自定义代码", "将原始候选交给结果页中的 JavaScript 评分/排序函数。", "custom", 0.75, 0.25, false, 1, "custom", true)
    };

    public static IReadOnlyList<SearchPresetDefinition> GetVisible(PluginConfiguration configuration)
    {
        var custom = ParseCustom(configuration.SearchCustomPresetsJson);
        var all = BuiltIns.Concat(custom).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var requested = (configuration.SearchVisiblePresetIds ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0) return all.Values.ToArray();
        var visible = requested.Where(all.ContainsKey).Select(id => all[id]).ToList();
        return visible.Count == 0 ? BuiltIns : visible;
    }

    public static SearchPresetDefinition Resolve(PluginConfiguration configuration, string? id)
    {
        var visible = GetVisible(configuration);
        if (!string.IsNullOrWhiteSpace(id))
        {
            var match = visible.FirstOrDefault(x => string.Equals(x.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return visible.FirstOrDefault(x => string.Equals(x.Id, configuration.DefaultSearchPresetId, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault()
            ?? BuiltIns[0];
    }

    private static IReadOnlyList<SearchPresetDefinition> ParseCustom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SearchPresetDefinition>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<SearchPresetDefinition>();
            var result = new List<SearchPresetDefinition>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var presetId = id.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(presetId) || BuiltIns.Any(x => string.Equals(x.Id, presetId, StringComparison.OrdinalIgnoreCase))) continue;
                var name = ReadString(item, "name", presetId);
                var description = ReadString(item, "description", "用户自定义搜索配置");
                var mode = ReadString(item, "mode", "weighted").ToLowerInvariant();
                var visual = ReadDouble(item, "visualWeight", 0.75);
                var title = ReadDouble(item, "titleWeight", 0.25);
                var penaltyEnabled = ReadBool(item, "applyMissingModalityPenalty", false);
                var penalty = Math.Clamp(ReadDouble(item, "missingModalityPenalty", 0.65), 0, 1);
                var sort = ReadString(item, "sortBy", "score").ToLowerInvariant();
                result.Add(new SearchPresetDefinition(presetId, name, description, mode, visual, title, penaltyEnabled, penalty, sort));
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<SearchPresetDefinition>();
        }
    }

    private static string ReadString(JsonElement item, string name, string fallback)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : fallback;

    private static double ReadDouble(JsonElement item, string name, double fallback)
        => item.TryGetProperty(name, out var value) && value.TryGetDouble() && double.IsFinite(value.GetDouble()) ? value.GetDouble() : fallback;

    private static bool ReadBool(JsonElement item, string name, bool fallback)
        => item.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? value.GetBoolean() : fallback;
}
