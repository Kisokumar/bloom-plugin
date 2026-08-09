using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class UpdateMeilisearchIndexTask(
    ILogger<UpdateMeilisearchIndexTask> logger,
    Indexer indexer,
    MeilisearchClientHolder clientHolder)
    : ILibraryPostScanTask
{
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating Meilisearch index");
        if (!clientHolder.Ok)
        {
            logger.LogError("Meilisearch is not configured, skipping index update");
            return;
        }

        try
        {
            await indexer.Index();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to update Meilisearch index");
        }
    }
}