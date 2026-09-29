using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.VisualSearch;

/// <summary>Direct cloud Embedding and Qdrant client. No Worker process is required.</summary>
public sealed class VisualSearchClient
{
    private readonly HttpClient _http;

    public VisualSearchClient(HttpClient http) => _http = http;

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var vector = await EmbedTextAsync("health check", cancellationToken).ConfigureAwait(false);
        using var qdrant = await _http.GetAsync(config.QdrantUrl.TrimEnd('/') + "/healthz", cancellationToken).ConfigureAwait(false);
        qdrant.EnsureSuccessStatusCode();
        return new WorkerHealth { Status = "ready", Provider = "cloud", Model = config.EmbeddingModel, ModelVersion = config.EmbeddingModel, Dimension = vector.Length, Device = "cloud" };
    }

    public Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken) => EmbedAsync(text, "text", cancellationToken);

    public Task<float[]> EmbedImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        var mime = DetectMime(image);
        return EmbedAsync($"data:{mime};base64,{Convert.ToBase64String(image)}", "image", cancellationToken);
    }

    private async Task<float[]> EmbedAsync(string value, string kind, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.EmbeddingBaseUrl)) throw new InvalidOperationException("Embedding Base URL is not configured");
        if (string.IsNullOrWhiteSpace(config.EmbeddingModel)) throw new InvalidOperationException("Embedding model is not configured");
        var endpoint = config.EmbeddingBaseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)) endpoint += "/embeddings";
        object item = kind == "text"
            ? new { type = "text", text = value }
            : new { type = "image_url", image_url = new { url = value } };
        object body = config.EmbeddingProtocol.Equals("plain", StringComparison.OrdinalIgnoreCase)
            ? new { model = config.EmbeddingModel, input = value }
            : new { model = config.EmbeddingModel, input = new object[] { item } };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config.EmbeddingApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.EmbeddingApiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var vector = ReadVector(document.RootElement);
        if (config.EmbeddingDimension > 0 && vector.Length != config.EmbeddingDimension) throw new InvalidDataException($"Embedding dimension {vector.Length} does not match configured {config.EmbeddingDimension}");
        return vector;
    }

    public async Task UpsertAsync(string collection, IEnumerable<object> points, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await _http.PutAsJsonAsync(config.QdrantUrl.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(collection) + "/points?wait=true", new { points }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnsureCollectionsAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var body = new { vectors = new { size = config.EmbeddingDimension, distance = "Cosine" } };
        foreach (var collection in new[] { "jellyfin_video_text", "jellyfin_video_frames" })
        {
            using var response = await _http.PutAsJsonAsync(config.QdrantUrl.TrimEnd('/') + "/collections/" + collection, body, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlyList<RemoteSearchHit>> SearchAsync(float[] vector, string collection, int limit, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await _http.PostAsJsonAsync(config.QdrantUrl.TrimEnd('/') + "/collections/" + Uri.EscapeDataString(collection) + "/points/search", new { vector, limit, with_payload = true }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var hits = new List<RemoteSearchHit>();
        if (!document.RootElement.TryGetProperty("result", out var result)) return hits;
        foreach (var hit in result.EnumerateArray())
        {
            if (!hit.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("itemId", out var id)) continue;
            hits.Add(new RemoteSearchHit(id.GetString() ?? string.Empty, hit.GetProperty("score").GetDouble(), payload));
        }
        return hits;
    }

    private static PluginConfiguration GetConfig() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static float[] ReadVector(JsonElement body)
    {
        JsonElement? value = null;
        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0) value = data[0].GetProperty("embedding");
        if (value is null && body.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object && output.TryGetProperty("embeddings", out var embeddings) && embeddings.GetArrayLength() > 0)
        {
            var first = embeddings[0]; value = first.ValueKind == JsonValueKind.Object ? first.GetProperty(first.TryGetProperty("embedding", out _) ? "embedding" : "vector") : first;
        }
        if (value is null && body.TryGetProperty("embedding", out var direct)) value = direct;
        if (value is null && body.TryGetProperty("vector", out var vector)) value = vector;
        if (value is null || value.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Embedding response does not contain a vector");
        var result = new float[value.Value.GetArrayLength()]; var i = 0;
        foreach (var item in value.Value.EnumerateArray()) result[i++] = item.GetSingle();
        return result;
    }

    private static string DetectMime(byte[] image) => image.Length >= 8 && image[0] == 0x89 && image[1] == 0x50 ? "image/png" : image.Length >= 6 && image[0] == 0x47 && image[1] == 0x49 ? "image/gif" : image.Length >= 12 && image[0] == 0x52 && image[1] == 0x49 && image[8] == 0x57 ? "image/webp" : "image/jpeg";
}

public sealed record WorkerHealth
{
    public string Status { get; init; } = "unknown";
    public string Provider { get; init; } = "cloud";
    public string Model { get; init; } = "unknown";
    public string ModelVersion { get; init; } = "unknown";
    public int Dimension { get; init; }
    public string Device { get; init; } = "cloud";
}
public sealed record RemoteSearchHit(string ItemId, double Score, JsonElement Payload);
