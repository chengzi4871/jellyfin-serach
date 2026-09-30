using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Linq;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.VisualSearch;

/// <summary>Direct cloud Embedding and Qdrant client. No Worker process is required.</summary>
public sealed class VisualSearchClient
{
    private readonly HttpClient _http;

    public VisualSearchClient(HttpClient http) => _http = http;

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        float[] vector;
        try
        {
            vector = await EmbedTextAsync("health check", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException)
        {
            throw new VisualSearchHealthException("cloud", ex.Message, ex, ex is InvalidOperationException or InvalidDataException ? 400 : 503);
        }

        if (string.IsNullOrWhiteSpace(config.QdrantUrl))
            throw new VisualSearchHealthException("qdrant", "Qdrant URL is not configured. In Docker, use the host or Qdrant container address instead of 127.0.0.1.", null, 400);
        try
        {
            using var qdrant = await _http.GetAsync(config.QdrantUrl.TrimEnd('/') + "/healthz", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessWithDetailsAsync(qdrant, "Qdrant health check").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            throw new VisualSearchHealthException("qdrant", ex.Message, ex, 503);
        }
        return new WorkerHealth { Status = "ready", Provider = "cloud", Model = config.EmbeddingModel, ModelVersion = config.EmbeddingModel, Dimension = vector.Length, Device = "cloud" };
    }

    public Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken) => EmbedAsync(text, "text", cancellationToken);

    public Task<float[]> EmbedImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        image = PrepareImage(image);
        var mime = DetectMime(image);
        return EmbedAsync($"data:{mime};base64,{Convert.ToBase64String(image)}", "image", cancellationToken);
    }

    public static bool IsRetryable(Exception exception)
    {
        if (exception is TaskCanceledException or TimeoutException or IOException or SocketException) return true;
        if (exception is HttpRequestException http)
        {
            var status = (int?)http.StatusCode;
            return status is null or 408 or 425 or 429 or >= 500;
        }
        return false;
    }

    private async Task<float[]> EmbedAsync(string value, string kind, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.EmbeddingBaseUrl)) throw new InvalidOperationException("Embedding Base URL is not configured");
        if (string.IsNullOrWhiteSpace(config.EmbeddingModel)) throw new InvalidOperationException("Embedding model is not configured");
        var endpoint = config.EmbeddingBaseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)) endpoint += "/embeddings";
        var bodies = BuildBodies(config, value, kind).ToArray();
        string? lastError = null;
        foreach (var body in bodies)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(config.EmbeddingApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.EmbeddingApiKey);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                lastError = await ReadErrorAsync(response, "Embedding API").ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    throw new HttpRequestException($"Embedding API rejected the image body with HTTP 413 (Payload Too Large). The plugin downsampled images to {GetConfig().EmbeddingMaxImageDimension}px; reduce that setting or increase the provider body limit. Response: {lastError}", null, response.StatusCode);
                // A provider may reject one input shape while accepting another. Retry only 400s;
                // authentication, rate limit and server errors must be surfaced immediately.
                if (response.StatusCode == HttpStatusCode.BadRequest && body != bodies[^1]) continue;
                throw new HttpRequestException(lastError, null, response.StatusCode);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var vector = ReadVector(document.RootElement);
            if (config.EmbeddingDimension > 0 && vector.Length != config.EmbeddingDimension) throw new InvalidDataException($"Embedding dimension mismatch: API returned {vector.Length}, configured {config.EmbeddingDimension}. The request includes dimensions={config.EmbeddingDimension}; check whether this model supports that dimension.");
            return vector;
        }
        throw new HttpRequestException(lastError ?? "Embedding API rejected every supported input shape", null, HttpStatusCode.BadRequest);
    }

    private static IEnumerable<Dictionary<string, object?>> BuildBodies(PluginConfiguration config, string value, string kind)
    {
        var shape = config.EmbeddingInputShape?.Trim().ToLowerInvariant() ?? "auto";
        var protocol = config.EmbeddingProtocol?.Trim().ToLowerInvariant() ?? "openai_multimodal";
        var bodies = new List<Dictionary<string, object?>>();
        void Add(object input)
        {
            var body = new Dictionary<string, object?> { ["model"] = config.EmbeddingModel, ["input"] = input };
            if (config.EmbeddingDimension > 0) body["dimensions"] = config.EmbeddingDimension;
            bodies.Add(body);
        }
        if (protocol == "plain" || shape == "plain") Add(value);
        else if (shape == "openai") Add(new object[] { kind == "text" ? new Dictionary<string, object?> { ["type"] = "text", ["text"] = value } : new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = value } } });
        else if (shape == "provider") Add(new object[] { kind == "text" ? new Dictionary<string, object?> { ["text"] = value } : new Dictionary<string, object?> { ["image"] = value } });
        else
        {
            // Inferera and several multimodal APIs use this compact shape.
            Add(new object[] { kind == "text" ? new Dictionary<string, object?> { ["text"] = value } : new Dictionary<string, object?> { ["image"] = value } });
            Add(value);
            Add(new object[] { kind == "text" ? new Dictionary<string, object?> { ["type"] = "text", ["text"] = value } : new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = value } } });
        }
        return bodies;
    }

    public async Task UpsertAsync(string collection, IEnumerable<object> points, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await _http.PutAsJsonAsync(GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points?wait=true", new { points }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant upsert ({collection})").ConfigureAwait(false);
    }

    public async Task EnsureCollectionsAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.QdrantUrl)) throw new InvalidOperationException("Qdrant URL is not configured");
        var body = new { vectors = new { size = config.EmbeddingDimension, distance = "Cosine" } };
        foreach (var collection in new[] { "jellyfin_video_text", "jellyfin_video_frames" })
        {
            var url = GetQdrantUrl(config) + "/collections/" + collection;
            using var existing = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (existing.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await existing.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                if (TryReadCollectionDimension(document.RootElement, out var existingDimension) && config.EmbeddingDimension > 0 && existingDimension != config.EmbeddingDimension)
                    throw new InvalidOperationException($"Qdrant collection {collection} uses dimension {existingDimension}, but the current embedding configuration is {config.EmbeddingDimension}. Recreate the collection before indexing with this model dimension.");
                continue;
            }
            if (existing.StatusCode != HttpStatusCode.NotFound)
                await EnsureSuccessWithDetailsAsync(existing, $"Qdrant collection check ({collection})").ConfigureAwait(false);

            using var response = await _http.PutAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict) continue;
            await EnsureSuccessWithDetailsAsync(response, $"Qdrant collection create ({collection})").ConfigureAwait(false);
        }
    }

    private static async Task EnsureSuccessWithDetailsAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await ReadErrorAsync(response, operation).ConfigureAwait(false);
        throw new HttpRequestException(detail, null, response.StatusCode);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string operation)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (body.Length > 500) body = body[..500];
        return $"{operation} failed with HTTP {(int)response.StatusCode} ({response.StatusCode}): {body}";
    }

    private static bool TryReadCollectionDimension(JsonElement body, out int dimension)
    {
        dimension = 0;
        return body.TryGetProperty("result", out var result)
            && result.TryGetProperty("config", out var config)
            && config.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("vectors", out var vectors)
            && vectors.TryGetProperty("size", out var size)
            && size.TryGetInt32(out dimension);
    }

    public async Task<IReadOnlyList<RemoteSearchHit>> SearchAsync(float[] vector, string collection, int limit, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await _http.PostAsJsonAsync(GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/search", new { vector, limit, with_payload = true }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant search ({collection})").ConfigureAwait(false);
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

    private static string GetQdrantUrl(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.QdrantUrl)) throw new InvalidOperationException("Qdrant URL is not configured. In Docker, use the host or Qdrant container address instead of 127.0.0.1.");
        return config.QdrantUrl.TrimEnd('/');
    }

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
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Embedding response does not contain a vector array. Actual value type: {value?.Value.ValueKind.ToString() ?? "missing"}. Response: {Truncate(body.GetRawText())}");
        var result = new float[value.Value.GetArrayLength()]; var i = 0;
        foreach (var item in value.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var number)) result[i++] = number;
            else if (item.ValueKind == JsonValueKind.String && float.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) result[i++] = parsed;
            else throw new InvalidDataException($"Embedding vector contains unsupported element type {item.ValueKind} at index {i}. Response: {Truncate(body.GetRawText())}");
        }
        return result;
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    private static byte[] PrepareImage(byte[] image)
    {
        var maxDimension = Math.Clamp(GetConfig().EmbeddingMaxImageDimension, 128, 4096);
        try
        {
            using var input = Image.Load<Rgba32>(image);
            if (Math.Max(input.Width, input.Height) <= maxDimension && image.Length <= 1024 * 1024) return image;
            var scale = Math.Min(1d, maxDimension / (double)Math.Max(input.Width, input.Height));
            var width = Math.Max(1, (int)Math.Round(input.Width * scale));
            var height = Math.Max(1, (int)Math.Round(input.Height * scale));
            input.Mutate(ctx => ctx.Resize(width, height));
            using var output = new MemoryStream();
            input.SaveAsJpeg(output, new JpegEncoder { Quality = 82 });
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Unable to prepare image for Embedding API: {ex.Message}", ex);
        }
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
public sealed class VisualSearchHealthException : Exception
{
    public VisualSearchHealthException(string stage, string message, Exception? innerException, int statusCode)
        : base(message, innerException) { Stage = stage; StatusCode = statusCode; }

    public string Stage { get; }
    public int StatusCode { get; }
}
