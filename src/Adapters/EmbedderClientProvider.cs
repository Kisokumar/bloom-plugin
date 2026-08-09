using Jellyfin.Plugin.Meilisearch.Semantic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// Bridges plugin configuration to the semantic core: hands out an
/// EmbedderClient + SemanticIndexer pair for the currently configured
/// EmbedderUrl, rebuilding when the URL changes. Null when semantic search
/// is unconfigured or disabled; callers treat that as "stock upstream".
/// </summary>
public class EmbedderClientProvider(ILoggerFactory loggerFactory)
{
    private static readonly HttpClient Http = new();

    private readonly object _lock = new();
    private EmbedderClient? _client;
    private SemanticIndexer? _indexer;
    private string? _key;

    public (EmbedderClient Client, SemanticIndexer Indexer)? Get()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { SemanticEnabled: true } || string.IsNullOrWhiteSpace(config.EmbedderUrl))
            return null;

        var model = string.IsNullOrWhiteSpace(config.EmbedderModel) ? "bge-small-en-v1.5" : config.EmbedderModel;
        var key = $"{config.EmbedderUrl}|{model}";
        lock (_lock)
        {
            if (_client == null || _indexer == null || _key != key)
            {
                _key = key;
                _client = new EmbedderClient(Http, config.EmbedderUrl, loggerFactory.CreateLogger<EmbedderClient>(), model);
                _indexer = new SemanticIndexer(_client, $"{model}-int8/1", loggerFactory.CreateLogger<SemanticIndexer>());
            }

            return (_client, _indexer);
        }
    }
}
