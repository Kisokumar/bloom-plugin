using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// Manual trigger for the (idempotent) index run, which embeds any
/// missing/stale vectors via HybridEfCoreIndexer.PostIndexAsync.
/// </summary>
public class SemanticBackfillTask(ILogger<SemanticBackfillTask> logger, Indexer indexer) : IScheduledTask
{
    public string Name => "Semantic backfill (embed missing vectors)";
    public string Key => "task-meilisearch-semantic-backfill";
    public string Description => "Re-index all documents and embed missing or stale vectors via the sidecar";
    public string Category => "Bloom";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        logger.LogInformation("Semantic backfill triggered");
        await indexer.Index();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
