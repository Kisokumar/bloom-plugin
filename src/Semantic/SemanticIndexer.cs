using System.Text.Json.Serialization;
using Meilisearch;
using Meilisearch.QueryParameters;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Pushes vectors for stale documents only. Runs after the base BM25 push
/// (which uses update semantics, so existing vectors survive). Never throws:
/// vector sync failing must not break keyword indexing.
/// </summary>
public class SemanticIndexer(EmbedderClient embedder, string modelId, ILogger<SemanticIndexer> logger)
{
    public string ModelId => modelId;

    public const string EmbedderName = "default";
    private const int FetchPage = 2000;
    private const int PushBatch = 500;

    private static readonly HttpClient SettingsHttp = new();

    // The daily index task and the backfill task both fire at midnight; two
    // concurrent syncs saturate the sidecar until both degrade. Single-flight:
    // the second trigger is a no-op.
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public async Task<(int Embedded, int Skipped)> SyncVectorsAsync(
        Index index, string? meiliUrl, string? meiliApiKey,
        IReadOnlyList<MeilisearchItem> items, CancellationToken cancellationToken = default)
    {
        if (!await SyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Semantic sync already running, skipping duplicate trigger");
            return (0, items.Count);
        }

        try
        {
            return await SyncInternalAsync(index, meiliUrl, meiliApiKey, items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Semantic vector sync failed, BM25 index is unaffected");
            return (0, items.Count);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    private async Task<(int, int)> SyncInternalAsync(
        Index index, string? meiliUrl, string? meiliApiKey,
        IReadOnlyList<MeilisearchItem> items, CancellationToken cancellationToken)
    {
        var existing = await FetchExistingHashesAsync(index, cancellationToken).ConfigureAwait(false);

        var stale = new List<(MeilisearchItem Item, string Text, string Hash)>();
        foreach (var item in items)
        {
            var text = IndexDocumentBuilder.BuildEmbedText(item);
            var hash = IndexDocumentBuilder.ComputeEmbedHash(text, modelId);
            if (!existing.TryGetValue(item.Guid, out var current) || current != hash)
                stale.Add((item, text, hash));
        }

        if (stale.Count == 0)
        {
            logger.LogInformation("Semantic index up to date ({Count} items)", items.Count);
            await TryConfigureEmbedderAsync(meiliUrl, meiliApiKey, index.Uid).ConfigureAwait(false);
            return (0, items.Count);
        }

        logger.LogInformation("Embedding {Stale} stale of {Total} items", stale.Count, items.Count);
        var embedded = 0;
        for (var i = 0; i < stale.Count; i += EmbedderClient.MaxBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = stale.Skip(i).Take(EmbedderClient.MaxBatch).ToList();
            var vectors = await embedder
                .EmbedPassagesAsync(chunk.Select(c => c.Text).ToList(), cancellationToken)
                .ConfigureAwait(false);
            if (vectors == null)
            {
                logger.LogWarning("Embedder unavailable after {Done}/{Stale}, will resume next index run",
                    embedded, stale.Count);
                break;
            }

            var docs = chunk.Select((c, j) => new VectorDocument(
                c.Item.Guid,
                modelId,
                c.Hash,
                new Dictionary<string, float[]> { [EmbedderName] = vectors[j] })).ToList();

            await index.UpdateDocumentsInBatchesAsync(docs, PushBatch, "guid").ConfigureAwait(false);
            embedded += chunk.Count;
            if (embedded % 1024 < EmbedderClient.MaxBatch)
                logger.LogInformation("Semantic backfill progress: {Done}/{Stale}", embedded, stale.Count);
        }

        logger.LogInformation("Semantic sync done: {Embedded} embedded, {Fresh} already fresh",
            embedded, items.Count - stale.Count);

        // Configure the embedder AFTER vectors are pushed: Meilisearch rejects a
        // userProvided embedder while any document lacks _vectors, so on a large
        // fresh index this only succeeds once the backfill has covered everything.
        // Attempted every run; a failure now just means "not all covered yet".
        if (embedded == stale.Count)
            await TryConfigureEmbedderAsync(meiliUrl, meiliApiKey, index.Uid).ConfigureAwait(false);

        return (embedded, items.Count - stale.Count);
    }

    /// <summary>
    /// Raw REST call: the pinned SDK's UpdateEmbeddersAsync silently sends nothing
    /// against current Meilisearch versions (observed against v1.34 in prod).
    /// </summary>
    private async Task TryConfigureEmbedderAsync(string? meiliUrl, string? meiliApiKey, string indexUid)
    {
        if (string.IsNullOrWhiteSpace(meiliUrl))
            return;
        try
        {
            var baseUrl = meiliUrl.TrimEnd('/');
            using var get = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/indexes/{indexUid}/settings/embedders");
            Authorize(get, meiliApiKey);
            using var current = await SettingsHttp.SendAsync(get).ConfigureAwait(false);
            if (current.IsSuccessStatusCode)
            {
                var body = await current.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Contains($"\"{EmbedderName}\"", StringComparison.Ordinal))
                    return; // already configured
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [EmbedderName] = new { source = "userProvided", dimensions = EmbedderClient.Dimension },
            });
            using var patch = new HttpRequestMessage(HttpMethod.Patch,
                $"{baseUrl}/indexes/{indexUid}/settings/embedders")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            Authorize(patch, meiliApiKey);
            using var response = await SettingsHttp.SendAsync(patch).ConfigureAwait(false);
            logger.LogInformation("Embedder settings apply for '{Index}': HTTP {Status}",
                indexUid, (int)response.StatusCode);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Embedder settings apply failed, will retry on next index run");
        }
    }

    private static void Authorize(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static async Task<Dictionary<string, string>> FetchExistingHashesAsync(
        Index index, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await index.GetDocumentsAsync<HashDocument>(new DocumentsQuery
            {
                Fields = ["guid", "embedHash"],
                Limit = FetchPage,
                Offset = offset,
            }, cancellationToken).ConfigureAwait(false);

            var results = page.Results?.ToList() ?? [];
            foreach (var doc in results)
            {
                if (doc.Guid != null && doc.EmbedHash != null)
                    hashes[doc.Guid] = doc.EmbedHash;
            }

            offset += results.Count;
            if (results.Count < FetchPage)
                return hashes;
        }
    }

    internal sealed record VectorDocument(
        [property: JsonPropertyName("guid")] string Guid,
        [property: JsonPropertyName("semanticModel")] string SemanticModel,
        [property: JsonPropertyName("embedHash")] string EmbedHash,
        [property: JsonPropertyName("_vectors")] Dictionary<string, float[]> Vectors);

    private sealed record HashDocument(
        [property: JsonPropertyName("guid")] string? Guid,
        [property: JsonPropertyName("embedHash")] string? EmbedHash);
}
