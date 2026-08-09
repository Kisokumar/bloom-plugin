using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class ScheduledTask(ILogger<ScheduledTask> logger, Indexer indexer) : IScheduledTask
{
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        logger.LogInformation("Executing meilisearch index task");
        await indexer.Index();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public string Name => "Rebuild search index";
    public string Key => "task-meilisearch-reindex-full";
    public string Description => "Rebuild the search index for all library items";
    public string Category => "Bloom";
}