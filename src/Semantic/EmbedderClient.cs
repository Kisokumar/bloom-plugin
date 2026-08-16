using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Client for the embedding sidecar. Every failure (timeout, non-2xx,
/// malformed body, wrong dimension) returns null; this class never throws into
/// the (sync-over-async) search path. Query embeddings are LRU-cached.
/// </summary>
public class EmbedderClient(HttpClient httpClient, string baseUrl, ILogger<EmbedderClient> logger, string? model = null)
{
    public string? Model => model;

    public const int Dimension = 384;
    public const int MaxBatch = 64;
    private const int QueryCacheCapacity = 1024;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PassageTimeout = TimeSpan.FromSeconds(30);

    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private readonly LruCache<string, float[]> _queryCache = new(QueryCacheCapacity);

    public string? LastModelId { get; private set; }

    public async Task<float[]?> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_queryCache.TryGet(query, out var cached))
            return cached;

        var vectors = await PostEmbedAsync([query], "query", QueryTimeout, cancellationToken).ConfigureAwait(false);
        var vector = vectors is { Count: 1 } ? vectors[0] : null;
        if (vector != null)
            _queryCache.Put(query, vector);
        return vector;
    }

    /// <summary>Embeds passages in ≤64-sized chunks. Null if any chunk fails.</summary>
    public async Task<IReadOnlyList<float[]>?> EmbedPassagesAsync(
        IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        if (passages.Count == 0)
            return [];

        var all = new List<float[]>(passages.Count);
        for (var i = 0; i < passages.Count; i += MaxBatch)
        {
            var chunk = passages.Skip(i).Take(MaxBatch).ToList();
            var vectors = await PostEmbedAsync(chunk, "passage", PassageTimeout, cancellationToken).ConfigureAwait(false);
            if (vectors == null || vectors.Count != chunk.Count)
                return null;
            all.AddRange(vectors);
        }

        return all;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(QueryTimeout);
            using var response = await httpClient.GetAsync($"{_baseUrl}/health", cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<float[]>?> PostEmbedAsync(
        IReadOnlyList<string> texts, string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var response = await httpClient
                .PostAsJsonAsync($"{_baseUrl}/embed", new EmbedRequest(texts, kind, model), cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Embedder returned {Status} for {Count} {Kind} texts",
                    (int)response.StatusCode, texts.Count, kind);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(cts.Token).ConfigureAwait(false);
            if (body?.Vectors == null || body.Vectors.Count != texts.Count
                || body.Vectors.Any(v => v is not { Length: Dimension }))
            {
                logger.LogWarning("Embedder response malformed (expected {Count}×{Dim})", texts.Count, Dimension);
                return null;
            }

            LastModelId = body.Model;
            return body.Vectors;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Embedder call failed ({Kind}, {Count} texts), degrading", kind, texts.Count);
            return null;
        }
    }

    public async Task<string?> ListModelsRawAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(QueryTimeout);
            return await httpClient.GetStringAsync($"{_baseUrl}/models", cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("model")] string? Model);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("vectors")] List<float[]>? Vectors,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("dim")] int Dim);
}
