using System.Collections.Immutable;
using System.Globalization;
using Meilisearch;
using Meilisearch.QueryParameters;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public abstract class Indexer(MeilisearchClientHolder clientHolder, ILogger<Indexer> logger)
{
    public Dictionary<string, string> Status { get; } = new();

    public async Task Index()
    {
        var task = clientHolder.Call(IndexInternal);
        if (task == null)
        {
            logger.LogWarning("Meilisearch is not configured, skipping index update");
            return;
        }

        await task;
    }

    private async Task IndexInternal(MeilisearchClient meilisearchClient, Index index)
    {
        var items = await GetItems(TypeHelper.TypeFullNames);

        if (items.Count <= 0)
        {
            logger.LogInformation("No items to index");
            return;
        }

        // Update (not replace) semantics: replacing docs would wipe the _vectors
        // pushed separately by the semantic indexer.
        await index.UpdateDocumentsInBatchesAsync(items, batchSize: 5000, primaryKey: "guid");
        logger.LogInformation("Upload {COUNT} items to Meilisearch", items.Count);
        Status["Items"] = items.Count.ToString();
        Status["LastIndexed"] = DateTime.Now.ToString(CultureInfo.CurrentCulture);
        await SweepRemovedAsync(index, items);
        await PostIndexAsync(meilisearchClient, index, items);
    }

    /// <summary>
    /// Delete documents the library no longer contains.
    ///
    /// Indexing is add-or-update so that separately-pushed vectors survive, which
    /// also means it can never remove anything. Deletions normally arrive through
    /// the repository decorator; this catches everything that did not -- items
    /// removed while the plugin was down, a restored Meilisearch snapshot, a
    /// failed delete. Without it a "rebuild" reconciles edits but not removals.
    ///
    /// Safe against a partially-applied upload: only ids absent from the source
    /// set are deleted, so anything still queued for indexing is protected.
    /// </summary>
    private async Task SweepRemovedAsync(Index index, ImmutableList<MeilisearchItem> items)
    {
        try
        {
            var live = items.Select(i => i.Guid).Where(g => g != null).ToHashSet();
            List<string> removed = [];
            const int page = 1000;
            for (var offset = 0; ; offset += page)
            {
                var batch = await index.GetDocumentsAsync<IndexedGuid>(
                    new DocumentsQuery { Limit = page, Offset = offset, Fields = ["guid"] });
                var results = batch.Results.ToList();
                if (results.Count == 0)
                    break;
                removed.AddRange(results.Select(r => r.Guid)
                    .Where(g => !string.IsNullOrEmpty(g) && !live.Contains(g))!);
                if (results.Count < page)
                    break;
            }

            if (removed.Count == 0)
                return;

            await index.DeleteDocumentsAsync(removed);
            logger.LogInformation("Removed {COUNT} documents no longer in the library", removed.Count);
            Status["Removed"] = removed.Count.ToString();
        }
        catch (Exception e)
        {
            // A failed sweep leaves stale documents, which is the status quo --
            // never let it fail the index run that just succeeded.
            logger.LogWarning(e, "Could not sweep removed documents");
        }
    }

    private sealed record IndexedGuid(
        [property: System.Text.Json.Serialization.JsonPropertyName("guid")] string? Guid);

    protected virtual Task PostIndexAsync(MeilisearchClient client, Index index, ImmutableList<MeilisearchItem> items)
        => Task.CompletedTask;

    protected abstract Task<ImmutableList<MeilisearchItem>> GetItems(IReadOnlySet<string> includedTypes);
}
