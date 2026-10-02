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
    private readonly Func<PluginConfiguration> _configurationProvider;
    private readonly object _queryCacheGate = new();
    private readonly Dictionary<string, QueryCacheEntry> _queryCache = new(StringComparer.Ordinal);
    private const int QueryCacheCapacity = 256;
    private static readonly TimeSpan QueryCacheLifetime = TimeSpan.FromMinutes(10);

    public VisualSearchClient(HttpClient http)
        : this(http, GetConfig) { }

    internal VisualSearchClient(HttpClient http, Func<PluginConfiguration> configurationProvider)
    {
        _http = http;
        _configurationProvider = configurationProvider;
    }

    public async Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var config = _configurationProvider();
        float[] vector;
        try
        {
            vector = await EmbedTextAsync("health check", cancellationToken).ConfigureAwait(false);
        }
        catch (VisualSearchConfigurationException ex)
        {
            throw new VisualSearchHealthException("cloud", ex.Message, ex, 400);
        }
        catch (HttpRequestException ex)
        {
            throw new VisualSearchHealthException("cloud", ex.Message, ex, 503);
        }
        catch (InvalidDataException ex)
        {
            throw new VisualSearchHealthException("cloud", ex.Message, ex, 502);
        }

        if (string.IsNullOrWhiteSpace(config.QdrantUrl))
            throw new VisualSearchHealthException("qdrant", "Qdrant URL is not configured. In Docker, use the host or Qdrant container address instead of 127.0.0.1.", null, 400);
        try
        {
            using var qdrant = await _http.GetAsync(config.QdrantUrl.TrimEnd('/') + "/healthz", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessWithDetailsAsync(qdrant, "Qdrant health check").ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new VisualSearchHealthException("qdrant", ex.Message, ex, 503);
        }
        return new WorkerHealth { Status = "ready", Provider = "cloud", Model = config.EmbeddingModel, ModelVersion = config.EmbeddingModel, Dimension = vector.Length, Device = "cloud" };
    }

    public Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken)
        => EmbedSingleAsync(text, "text", cancellationToken);

    /// <summary>Embeds a user query with a short bounded cache to avoid duplicate remote calls.</summary>
    public async Task<float[]> EmbedQueryTextAsync(string text, CancellationToken cancellationToken)
    {
        var normalized = NormalizeQuery(text);
        var config = _configurationProvider();
        var key = string.Join("\u001f", config.EmbeddingBaseUrl, config.EmbeddingModel, config.EmbeddingProtocol, config.EmbeddingInputShape, config.EmbeddingDimension, normalized);
        lock (_queryCacheGate)
        {
            if (_queryCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.CreatedAt < QueryCacheLifetime)
            {
                cached.LastAccessAt = DateTime.UtcNow;
                return cached.Vector;
            }
            _queryCache.Remove(key);
        }

        var vector = await EmbedTextAsync(normalized, cancellationToken).ConfigureAwait(false);
        lock (_queryCacheGate)
        {
            var now = DateTime.UtcNow;
            _queryCache[key] = new QueryCacheEntry(vector, now, now);
            while (_queryCache.Count > QueryCacheCapacity)
            {
                var oldest = _queryCache.OrderBy(x => x.Value.LastAccessAt).First().Key;
                _queryCache.Remove(oldest);
            }
        }
        return vector;
    }

    private static string NormalizeQuery(string text)
        => string.Join(' ', (text ?? string.Empty).Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken, Action<int, int, int>? batchProgress = null)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        return await EmbedBatchedAsync(texts, "text", cancellationToken, batchProgress).ConfigureAwait(false);
    }

    public Task<float[]> EmbedImageAsync(byte[] image, CancellationToken cancellationToken)
        => EmbedSingleImageAsync(image, cancellationToken);

    public async Task<IReadOnlyList<float[]>> EmbedImagesAsync(IReadOnlyList<byte[]> images, CancellationToken cancellationToken, Action<int, int, int>? batchProgress = null)
    {
        if (images.Count == 0) return Array.Empty<float[]>();
        // Keep raw frames until their batch is needed. EmbedBatchedAsync uses a
        // one-batch look-ahead, so image resizing/JPEG encoding for the next batch
        // runs while the current HTTP request is in flight.
        return await EmbedBatchedAsync(
            images.Count,
            "image",
            (offset, count, token) => PrepareImageBatchAsync(images, offset, count, token),
            cancellationToken,
            batchProgress).ConfigureAwait(false);
    }

    private async Task<float[]> EmbedSingleAsync(string value, string kind, CancellationToken cancellationToken)
    {
        var vectors = await EmbedBatchRequestAsync(new[] { value }, kind, cancellationToken).ConfigureAwait(false);
        return vectors[0];
    }

    private async Task<float[]> EmbedSingleImageAsync(byte[] image, CancellationToken cancellationToken)
    {
        var prepared = PrepareImage(image);
        var value = $"data:{DetectMime(prepared)};base64,{Convert.ToBase64String(prepared)}";
        return await EmbedSingleAsync(value, "image", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchedAsync(IReadOnlyList<string> values, string kind, CancellationToken cancellationToken, Action<int, int, int>? batchProgress)
    {
        return await EmbedBatchedAsync(
            values.Count,
            kind,
            (offset, count, _) => Task.FromResult<IReadOnlyList<string>>(values.Skip(offset).Take(count).ToArray()),
            cancellationToken,
            batchProgress).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends embedding batches in order while preparing at most one following
    /// batch in advance. There is still only one remote request in flight, but
    /// local preparation can overlap the current network/model request.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> EmbedBatchedAsync(
        int valueCount,
        string kind,
        Func<int, int, CancellationToken, Task<IReadOnlyList<string>>> prepareBatch,
        CancellationToken cancellationToken,
        Action<int, int, int>? batchProgress)
    {
        if (valueCount == 0) return Array.Empty<float[]>();

        var batchSize = Math.Clamp(_configurationProvider().EmbeddingBatchSize, 1, 256);
        var totalBatches = (int)Math.Ceiling(valueCount / (double)batchSize);
        var vectors = new List<float[]>(valueCount);
        var nextPrepared = prepareBatch(0, Math.Min(batchSize, valueCount), cancellationToken);

        for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            var offset = batchIndex * batchSize;
            var count = Math.Min(batchSize, valueCount - offset);
            var batch = await nextPrepared.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Task<IReadOnlyList<string>>? followingPrepared = null;
            if (batchIndex + 1 < totalBatches)
            {
                var followingOffset = offset + batchSize;
                var followingCount = Math.Min(batchSize, valueCount - followingOffset);
                // Start preparing the following batch before awaiting the current
                // remote request. The bounded look-ahead avoids unbounded memory
                // growth and preserves request ordering/backpressure.
                followingPrepared = prepareBatch(followingOffset, followingCount, cancellationToken);
            }

            try
            {
                vectors.AddRange(await EmbedBatchRequestAsync(batch, kind, cancellationToken).ConfigureAwait(false));
                batchProgress?.Invoke(batchIndex + 1, totalBatches, count);
            }
            catch
            {
                // Observe a look-ahead failure before propagating the request
                // failure; this prevents an unobserved preparation exception.
                await ObservePreparationAsync(followingPrepared).ConfigureAwait(false);
                throw;
            }

            nextPrepared = followingPrepared ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        return vectors;
    }

    private static async Task ObservePreparationAsync(Task<IReadOnlyList<string>>? preparation)
    {
        if (preparation is null) return;
        try { await preparation.ConfigureAwait(false); }
        catch { /* the active request/callback failure remains authoritative */ }
    }

    private static Task<IReadOnlyList<string>> PrepareImageBatchAsync(
        IReadOnlyList<byte[]> images,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var values = new string[count];
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = PrepareImage(images[offset + index]);
                values[index] = $"data:{DetectMime(prepared)};base64,{Convert.ToBase64String(prepared)}";
            }
            return values;
        }, cancellationToken);
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

    private async Task<IReadOnlyList<float[]>> EmbedBatchRequestAsync(IReadOnlyList<string> values, string kind, CancellationToken cancellationToken)
    {
        var config = _configurationProvider();
        if (values.Count == 0) return Array.Empty<float[]>();
        if (string.IsNullOrWhiteSpace(config.EmbeddingBaseUrl)) throw new VisualSearchConfigurationException("Embedding Base URL is not configured");
        if (string.IsNullOrWhiteSpace(config.EmbeddingModel)) throw new VisualSearchConfigurationException("Embedding model is not configured");
        if (config.EmbeddingDimension <= 0) throw new VisualSearchConfigurationException("Embedding dimension must be greater than 0. Set it to the same dimension as the Qdrant collections (for this deployment: 1024); leaving it at 0 omits the dimensions parameter and can return the model's native dimension instead.");
        var endpoint = config.EmbeddingBaseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)) endpoint += "/embeddings";
        var bodies = BuildBodies(config, values, kind).ToArray();
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
                    throw new HttpRequestException($"Embedding API rejected the request with HTTP 413 (Payload Too Large). Reduce EmbeddingBatchSize or increase the provider body limit. The plugin downsampled images to {_configurationProvider().EmbeddingMaxImageDimension}px; reduce that setting or increase the provider body limit. Response: {lastError}", null, response.StatusCode);
                // Only HTTP 400 is eligible for trying another provider input shape.
                if (response.StatusCode == HttpStatusCode.BadRequest && body != bodies[^1]) continue;
                throw new HttpRequestException(lastError, null, response.StatusCode);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var vectors = ReadVectors(document.RootElement, values.Count);
            if (config.EmbeddingDimension > 0)
            {
                foreach (var vector in vectors)
                {
                    if (vector.Length != config.EmbeddingDimension)
                        throw new InvalidDataException($"Embedding dimension mismatch: API returned {vector.Length}, configured {config.EmbeddingDimension}. The request includes dimensions={config.EmbeddingDimension}; check whether this model supports that dimension.");
                }
            }
            return vectors;
        }
        throw new HttpRequestException(lastError ?? "Embedding API rejected every supported input shape", null, HttpStatusCode.BadRequest);
    }

    private static IEnumerable<Dictionary<string, object?>> BuildBodies(PluginConfiguration config, IReadOnlyList<string> values, string kind)
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
        object ScalarOrArray() => values.Count == 1 ? values[0] : values.ToArray();
        object Objects(string field)
            => values.Select(value => (object)new Dictionary<string, object?> { [field] = value }).ToArray();
        if (protocol == "plain" || shape == "plain") Add(ScalarOrArray());
        else if (shape == "openai")
            Add(values.Select(value => (object)(kind == "text"
                ? new Dictionary<string, object?> { ["type"] = "text", ["text"] = value }
                : new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = value } })).ToArray());
        else if (shape == "provider") Add(Objects(kind == "text" ? "text" : "image"));
        else
        {
            // Inferera and several multimodal APIs use [{text:...}] / [{image:...}].
            Add(Objects(kind == "text" ? "text" : "image"));
            Add(ScalarOrArray());
            Add(values.Select(value => (object)(kind == "text"
                ? new Dictionary<string, object?> { ["type"] = "text", ["text"] = value }
                : new Dictionary<string, object?> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, object?> { ["url"] = value } })).ToArray());
        }
        return bodies;
    }

    public async Task UpsertAsync(string collection, IEnumerable<object> points, CancellationToken cancellationToken)
    {
        var config = _configurationProvider();
        using var response = await _http.PutAsJsonAsync(GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points?wait=true", new { points }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant upsert ({collection})").ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the persisted content signatures for one video's title point.
    /// Missing collections or points are treated as an unindexed video.
    /// </summary>
    public async Task<IndexedContentSignature?> GetIndexedSignatureAsync(string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var config = _configurationProvider();
        var body = new { ids = new[] { itemId }, with_payload = true, with_vector = false };
        using var response = await _http.PostAsJsonAsync(
            GetQdrantUrl(config) + "/collections/jellyfin_video_text/points",
            body,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessWithDetailsAsync(response, "Qdrant read index signature").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return null;
        var point = result.EnumerateArray().FirstOrDefault();
        if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return null;
        return new IndexedContentSignature(ReadPayloadString(payload, "textHash"), ReadPayloadString(payload, "visualHash"));
    }

    /// <summary>Updates payload metadata without re-embedding an unchanged vector.</summary>
    public async Task SetPayloadAsync(string collection, string itemId, object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var config = _configurationProvider();
        using var response = await _http.PostAsJsonAsync(
            GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/payload?wait=true",
            new { payload, points = new[] { itemId } },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant payload update ({collection})").ConfigureAwait(false);
    }

    private static string? ReadPayloadString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    /// <summary>Removes all points belonging to one Jellyfin item from a collection.</summary>
    public async Task DeleteByItemAsync(string collection, string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var config = _configurationProvider();
        var body = new
        {
            filter = new
            {
                must = new[]
                {
                    new { key = "itemId", match = new { value = itemId } }
                }
            }
        };
        using var response = await _http.PostAsJsonAsync(
            GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/delete?wait=true",
            body,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant delete ({collection})").ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes points for an item that are no longer part of the newly indexed
    /// point set. The new points must be upserted first, so an indexing retry
    /// never leaves the item without its current vectors.
    /// </summary>
    public async Task DeleteByItemExceptAsync(string collection, string itemId, IReadOnlyCollection<string> keepPointIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        var keep = new HashSet<string>(keepPointIds.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        var all = await ScrollPointIdsByItemAsync(collection, itemId, cancellationToken).ConfigureAwait(false);
        var stale = all.Where(x => !keep.Contains(x)).ToArray();
        foreach (var chunk in stale.Chunk(256))
        {
            var config = _configurationProvider();
            using var response = await _http.PostAsJsonAsync(
                GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/delete?wait=true",
                new { points = chunk },
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            await EnsureSuccessWithDetailsAsync(response, $"Qdrant stale-point delete ({collection})").ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> ScrollPointIdsByItemAsync(string collection, string itemId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        JsonElement? offset = null;
        while (true)
        {
            var config = _configurationProvider();
            var body = new Dictionary<string, object?>
            {
                ["filter"] = new
                {
                    must = new[] { new { key = "itemId", match = new { value = itemId } } }
                },
                ["limit"] = 1000,
                ["with_payload"] = false,
                ["with_vector"] = false
            };
            if (offset.HasValue) body["offset"] = offset.Value;
            using var response = await _http.PostAsJsonAsync(
                GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/scroll",
                body,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return result;
            await EnsureSuccessWithDetailsAsync(response, $"Qdrant scroll ({collection})").ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("result", out var scrollResult) || scrollResult.ValueKind != JsonValueKind.Object) break;
            if (scrollResult.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in points.EnumerateArray())
                {
                    if (!point.TryGetProperty("id", out var id)) continue;
                    var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
            }
            if (!scrollResult.TryGetProperty("next_page_offset", out var next) || next.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                break;
            offset = next.Clone();
        }
        return result;
    }

    public async Task EnsureCollectionsAsync(CancellationToken cancellationToken)
    {
        var config = _configurationProvider();
        if (string.IsNullOrWhiteSpace(config.QdrantUrl)) throw new VisualSearchConfigurationException("Qdrant URL is not configured");
        if (config.EmbeddingDimension <= 0) throw new VisualSearchConfigurationException("Embedding dimension must be greater than 0 before initializing Qdrant collections. Set it to the collection dimension (for this deployment: 1024), then initialize Qdrant again.");
        var body = new { vectors = new { size = config.EmbeddingDimension, distance = "Cosine" } };
        foreach (var collection in new[] { "jellyfin_video_text", "jellyfin_video_frames", "jellyfin_video_covers" })
        {
            var url = GetQdrantUrl(config) + "/collections/" + collection;
            using var existing = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (existing.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await existing.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                if (TryReadCollectionDimension(document.RootElement, out var existingDimension) && config.EmbeddingDimension > 0 && existingDimension != config.EmbeddingDimension)
                    throw new VisualSearchConfigurationException($"Qdrant collection '{collection}' dimension mismatch: actual dimension={existingDimension}, configured dimension={config.EmbeddingDimension}. Delete collection '{collection}' in Qdrant, then click '初始化 Qdrant' to recreate it before indexing.");
                await EnsurePayloadIndexAsync(url, "itemId", cancellationToken).ConfigureAwait(false);
                await EnsurePayloadIndexAsync(url, "libraryId", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (existing.StatusCode != HttpStatusCode.NotFound)
                await EnsureSuccessWithDetailsAsync(existing, $"Qdrant collection check ({collection})").ConfigureAwait(false);

            using var response = await _http.PutAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Conflict)
                await EnsureSuccessWithDetailsAsync(response, $"Qdrant collection create ({collection})").ConfigureAwait(false);
            await EnsurePayloadIndexAsync(url, "itemId", cancellationToken).ConfigureAwait(false);
            await EnsurePayloadIndexAsync(url, "libraryId", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsurePayloadIndexAsync(string collectionUrl, string fieldName, CancellationToken cancellationToken)
    {
        using var response = await _http.PutAsJsonAsync(
            collectionUrl + "/index?wait=true",
            new { field_name = fieldName, field_schema = "keyword" },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant payload index ({fieldName})").ConfigureAwait(false);
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

    public async Task<IReadOnlyList<RemoteSearchHit>> SearchAsync(
        float[] vector,
        string collection,
        int limit,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? libraryIds = null,
        int hnswEf = 0,
        bool exact = false,
        IReadOnlyList<string>? itemIds = null)
    {
        var config = _configurationProvider();
        var body = BuildSearchBody(vector, limit, libraryIds, itemIds, hnswEf, exact);
        using var response = await _http.PostAsJsonAsync(GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/search", body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant search ({collection})").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        return ParseSearchHits(document.RootElement);
    }

    /// <summary>
    /// Recalls a bounded number of videos rather than allowing one video's
    /// frames to consume the entire visual candidate budget. Qdrant 1.13.x
    /// groups by the indexed itemId payload field.
    /// </summary>
    public async Task<IReadOnlyList<RemoteSearchGroup>> SearchGroupedAsync(
        float[] vector,
        string collection,
        int groupLimit,
        int groupSize,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? libraryIds = null,
        int hnswEf = 0,
        bool exact = false)
    {
        var config = _configurationProvider();
        var body = BuildSearchBody(vector, groupLimit, libraryIds, null, hnswEf, exact);
        var grouped = new Dictionary<string, object?>(StringComparer.Ordinal);
        grouped["vector"] = vector;
        grouped["group_by"] = "itemId";
        grouped["limit"] = Math.Clamp(groupLimit, 1, 10000);
        grouped["group_size"] = Math.Clamp(groupSize, 1, 100);
        grouped["with_payload"] = true;
        if (body is Dictionary<string, object?> searchBody)
        {
            if (searchBody.TryGetValue("filter", out var filter)) grouped["filter"] = filter;
            if (searchBody.TryGetValue("params", out var parameters)) grouped["params"] = parameters;
        }
        using var response = await _http.PostAsJsonAsync(GetQdrantUrl(config) + "/collections/" + Uri.EscapeDataString(collection) + "/points/search/groups", grouped, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Keep the feature usable against a pre-1.13 server: the fallback
            // still limits the number of returned frames, then groups locally.
            var fallback = await SearchAsync(vector, collection, Math.Min(10000, Math.Max(1, groupLimit * groupSize)), cancellationToken, libraryIds, hnswEf, exact).ConfigureAwait(false);
            return fallback.GroupBy(x => x.ItemId, StringComparer.Ordinal)
                .Take(groupLimit)
                .Select(group => new RemoteSearchGroup(group.Key, group.Take(groupSize).ToArray()))
                .ToArray();
        }
        await EnsureSuccessWithDetailsAsync(response, $"Qdrant grouped search ({collection})").ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        return ParseGroupedSearchHits(document.RootElement, groupSize);
    }

    /// <summary>Grouped search for an optional collection such as the cover fallback collection.</summary>
    public async Task<IReadOnlyList<RemoteSearchGroup>> SearchGroupedOptionalAsync(
        float[] vector,
        string collection,
        int groupLimit,
        int groupSize,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? libraryIds = null,
        int hnswEf = 0,
        bool exact = false)
    {
        try
        {
            return await SearchGroupedAsync(vector, collection, groupLimit, groupSize, cancellationToken, libraryIds, hnswEf, exact).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<RemoteSearchGroup>();
        }
    }

    /// <summary>Searches an optional collection, treating a not-yet-created collection as empty.</summary>
    public async Task<IReadOnlyList<RemoteSearchHit>> SearchOptionalAsync(float[] vector, string collection, int limit, CancellationToken cancellationToken, IReadOnlyList<string>? libraryIds = null, int hnswEf = 0, bool exact = false, IReadOnlyList<string>? itemIds = null)
    {
        try
        {
            return await SearchAsync(vector, collection, limit, cancellationToken, libraryIds, hnswEf, exact, itemIds).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<RemoteSearchHit>();
        }
    }

    /// <summary>
    /// Parses Qdrant hits while copying payload values out of the response
    /// document. JsonElement is a view over JsonDocument storage, so returning
    /// the original element would leave a dangling reference after SearchAsync
    /// disposes the document.
    /// </summary>
    internal static IReadOnlyList<RemoteSearchHit> ParseSearchHits(JsonElement root)
    {
        var hits = new List<RemoteSearchHit>();
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) return hits;
        foreach (var hit in result.EnumerateArray())
        {
            if (!hit.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("itemId", out var id)
                || id.ValueKind != JsonValueKind.String
                || !hit.TryGetProperty("score", out var score)
                || !score.TryGetDouble(out var scoreValue))
            {
                continue;
            }

            // Clone is intentional: the returned record may be consumed after
            // the using JsonDocument scope in SearchAsync has ended.
            hits.Add(new RemoteSearchHit(id.GetString() ?? string.Empty, scoreValue, payload.Clone()));
        }
        return hits;
    }

    internal static IReadOnlyList<RemoteSearchGroup> ParseGroupedSearchHits(JsonElement root, int groupSize)
    {
        var groups = new List<RemoteSearchGroup>();
        if (!root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("groups", out var groupArray)
            || groupArray.ValueKind != JsonValueKind.Array)
            return groups;
        foreach (var group in groupArray.EnumerateArray())
        {
            if (!group.TryGetProperty("id", out var groupId)) continue;
            var groupText = groupId.ValueKind == JsonValueKind.String ? groupId.GetString() : groupId.ToString();
            if (string.IsNullOrWhiteSpace(groupText) || !group.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;
            var parsed = new List<RemoteSearchHit>();
            foreach (var hit in hits.EnumerateArray().Take(Math.Clamp(groupSize, 1, 100)))
            {
                var item = ParseSearchHit(hit);
                if (item is not null) parsed.Add(item);
            }
            if (parsed.Count > 0) groups.Add(new RemoteSearchGroup(groupText!, parsed));
        }
        return groups;
    }

    private static object BuildSearchBody(float[] vector, int limit, IReadOnlyList<string>? libraryIds, IReadOnlyList<string>? itemIds, int hnswEf, bool exact)
    {
        var body = new Dictionary<string, object?>
        {
            ["vector"] = vector,
            ["limit"] = Math.Clamp(limit, 1, 100000),
            ["with_payload"] = true
        };
        var filter = BuildFilter(libraryIds, itemIds);
        if (filter is not null) body["filter"] = filter;
        var parameters = BuildSearchParams(hnswEf, exact);
        if (parameters is not null) body["params"] = parameters;
        return body;
    }

    private static object? BuildFilter(IReadOnlyList<string>? libraryIds, IReadOnlyList<string>? itemIds)
    {
        var libraries = libraryIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        var items = itemIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        var must = new List<object>();
        if (libraries.Length > 0) must.Add(new { key = "libraryId", match = new { any = libraries } });
        if (items.Length > 0) must.Add(new { key = "itemId", match = new { any = items } });
        return must.Count == 0 ? null : new { must = must.ToArray() };
    }

    private static object? BuildSearchParams(int hnswEf, bool exact)
    {
        if (exact) return new { exact = true };
        return hnswEf > 0 ? new { hnsw_ef = Math.Clamp(hnswEf, 1, 10000) } : null;
    }

    private static RemoteSearchHit? ParseSearchHit(JsonElement hit)
    {
        if (!hit.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("itemId", out var id)
            || id.ValueKind != JsonValueKind.String
            || !hit.TryGetProperty("score", out var score)
            || !score.TryGetDouble(out var scoreValue)) return null;
        return new RemoteSearchHit(id.GetString() ?? string.Empty, scoreValue, payload.Clone());
    }

    private static PluginConfiguration GetConfig() => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string GetQdrantUrl(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.QdrantUrl)) throw new VisualSearchConfigurationException("Qdrant URL is not configured. In Docker, use the host or Qdrant container address instead of 127.0.0.1.");
        return config.QdrantUrl.TrimEnd('/');
    }

    private static IReadOnlyList<float[]> ReadVectors(JsonElement body, int expectedCount)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Embedding response is {body.ValueKind}, expected an object. Response: {Truncate(body.GetRawText())}");
        var vectorValues = new List<JsonElement>();
        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var entries = data.EnumerateArray().ToArray();
            if (entries.Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("index", out _)))
            {
                var ordered = new JsonElement[expectedCount];
                var seen = new bool[expectedCount];
                foreach (var item in entries)
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var indexValue)
                        || !indexValue.TryGetInt32(out var index) || index < 0 || index >= expectedCount || seen[index])
                        throw new InvalidDataException($"Embedding response has invalid, duplicate or missing input indexes. Response: {Truncate(body.GetRawText())}");
                    ordered[index] = item;
                    seen[index] = true;
                }
                if (seen.Any(value => !value))
                    throw new InvalidDataException($"Embedding response is missing input indexes. Response: {Truncate(body.GetRawText())}");
                entries = ordered;
            }
            foreach (var item in entries)
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("embedding", out var embedding)) vectorValues.Add(embedding);
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("vector", out var vector)) vectorValues.Add(vector);
            }
        }
        if (vectorValues.Count == 0 && body.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("embeddings", out var embeddings)
            && embeddings.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in embeddings.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("embedding", out var embedding)) vectorValues.Add(embedding);
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("vector", out var vector)) vectorValues.Add(vector);
                else vectorValues.Add(item);
            }
        }
        if (vectorValues.Count == 0 && body.TryGetProperty("embedding", out var direct)) vectorValues.Add(direct);
        if (vectorValues.Count == 0 && body.TryGetProperty("vector", out var directVector)) vectorValues.Add(directVector);
        if (vectorValues.Count != expectedCount)
            throw new InvalidDataException($"Embedding response returned {vectorValues.Count} vectors, expected {expectedCount}. The provider must return one vector per input; if it combines multimodal inputs, set Embedding batch size to 1. Response: {Truncate(body.GetRawText())}");

        return vectorValues.Select((value, index) => ReadVectorArray(value, index, body)).ToArray();
    }

    private static float[] ReadVectorArray(JsonElement value, int vectorIndex, JsonElement body)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Embedding response vector {vectorIndex} is {value.ValueKind}, expected an array. Response: {Truncate(body.GetRawText())}");
        if (value.GetArrayLength() == 0)
            throw new InvalidDataException($"Embedding vector {vectorIndex} is empty. Response: {Truncate(body.GetRawText())}");
        var result = new float[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var number)) result[i++] = number;
            else if (item.ValueKind == JsonValueKind.String && float.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) result[i++] = parsed;
            else throw new InvalidDataException($"Embedding vector {vectorIndex} contains unsupported element type {item.ValueKind} at index {i}. Response: {Truncate(body.GetRawText())}");
        }
        if (result.Any(number => !float.IsFinite(number)) || result.All(number => number == 0))
            throw new InvalidDataException($"Embedding vector {vectorIndex} contains non-finite values or is all zero. Response: {Truncate(body.GetRawText())}");
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
public sealed record IndexedContentSignature(string? TextHash, string? VisualHash);
public sealed record RemoteSearchHit(string ItemId, double Score, JsonElement Payload);
public sealed record RemoteSearchGroup(string ItemId, IReadOnlyList<RemoteSearchHit> Hits);
internal sealed class QueryCacheEntry
{
    public QueryCacheEntry(float[] vector, DateTime createdAt, DateTime lastAccessAt)
    {
        Vector = vector;
        CreatedAt = createdAt;
        LastAccessAt = lastAccessAt;
    }

    public float[] Vector { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastAccessAt { get; set; }
}
public sealed class VisualSearchHealthException : Exception
{
    public VisualSearchHealthException(string stage, string message, Exception? innerException, int statusCode)
        : base(message, innerException) { Stage = stage; StatusCode = statusCode; }

    public string Stage { get; }
    public int StatusCode { get; }
}

/// <summary>Raised only for values the administrator must correct in plugin configuration.</summary>
public sealed class VisualSearchConfigurationException : Exception
{
    public VisualSearchConfigurationException(string message)
        : base(message) { }

    public VisualSearchConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }
}
