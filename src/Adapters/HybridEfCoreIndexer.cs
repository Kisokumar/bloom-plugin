using System.Collections.Immutable;
using Jellyfin.Database.Implementations;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// Upstream EfCoreIndexer + a post-index vector sync (stale-only) when the
/// sidecar is configured. Vector failures never affect the BM25 push.
/// </summary>
public class HybridEfCoreIndexer(
    IJellyfinDatabaseProvider dbProvider,
    MeilisearchClientHolder clientHolder,
    EmbedderClientProvider embedderProvider,
    ILogger<EfCoreIndexer> logger
) : EfCoreIndexer(dbProvider, clientHolder, logger)
{
    protected override async Task PostIndexAsync(
        MeilisearchClient client, Index index, ImmutableList<MeilisearchItem> items)
    {
        if (embedderProvider.Get() is not var (_, semanticIndexer) || semanticIndexer is null)
            return;

        // Same env-first resolution the client holder uses; the raw embedder
        // settings call needs url+key because the SDK's own call is a no-op.
        var config = Plugin.Instance?.Configuration;
        var envUrl = Environment.GetEnvironmentVariable("MEILI_URL");
        var envKey = Environment.GetEnvironmentVariable("MEILI_MASTER_KEY");
        var meiliUrl = string.IsNullOrEmpty(envUrl) ? config?.Url : envUrl;
        var meiliKey = string.IsNullOrEmpty(envKey) ? config?.ApiKey : envKey;

        var (embedded, _) = await semanticIndexer
            .SyncVectorsAsync(index, meiliUrl, meiliKey, items).ConfigureAwait(false);
        Status["SemanticEmbedded"] = embedded.ToString();
        Status["SemanticModel"] = semanticIndexer.ModelId;
    }
}
