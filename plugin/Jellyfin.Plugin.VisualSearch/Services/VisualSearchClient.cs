using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.VisualSearch;

public sealed class VisualSearchClient
{
    private readonly HttpClient _http;

    public VisualSearchClient(HttpClient http) => _http = http;

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<WorkerHealth>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return value ?? new WorkerHealth();
    }

    public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/embed/text", new { text }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var vector = new List<float>();
        foreach (var value in document.RootElement.GetProperty("vector").EnumerateArray())
        {
            vector.Add(value.GetSingle());
        }
        return vector.ToArray();
    }

    public async Task<float[]> EmbedImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/embed/image", new { image_base64 = Convert.ToBase64String(image) }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var vector = new List<float>();
        foreach (var value in document.RootElement.GetProperty("vector").EnumerateArray()) vector.Add(value.GetSingle());
        return vector.ToArray();
    }

    public async Task UpsertAsync(string collection, IEnumerable<object> points, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/qdrant/upsert", new { collection, points }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnsureCollectionsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("/qdrant/ensure", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<RemoteSearchHit>> SearchAsync(float[] vector, string collection, int limit, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/qdrant/search", new { collection, vector, limit }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var hits = new List<RemoteSearchHit>();
        foreach (var hit in document.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = hit.TryGetProperty("payload", out var p) ? p : default;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("itemId", out var id)) continue;
            hits.Add(new RemoteSearchHit(id.GetString() ?? string.Empty, hit.GetProperty("score").GetDouble(), payload));
        }
        return hits;
    }
}

public sealed record WorkerHealth
{
    public string Status { get; init; } = "unknown";
    public string Model { get; init; } = "unknown";
    public string ModelVersion { get; init; } = "unknown";
    public int Dimension { get; init; }
    public string Device { get; init; } = "unknown";
}
public sealed record RemoteSearchHit(string ItemId, double Score, JsonElement Payload);
