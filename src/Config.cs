using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch;

public class Config : BasePluginConfiguration
{
    public Config()
    {
        ApiKey = string.Empty;
        Url = string.Empty;
        Debug = false;
        IndexName = string.Empty;
        MatchingStrategy = "last";
        EmbedderUrl = string.Empty;
        SemanticEnabled = true;
        EmbedderModel = "bge-small-en-v1.5";
        GatewayUrl = string.Empty;
    }

    public string ApiKey { get; set; }
    public string Url { get; set; }

    public bool Debug { get; set; }
    public string IndexName { get; set; }

    /// <summary>
    /// Meilisearch matchingStrategy: "last", "all", or "frequency".
    /// </summary>
    public string MatchingStrategy { get; set; }

    /// <summary>
    /// Embedding sidecar base URL (e.g. http://sidecar:8000). Empty = semantic search off,
    /// behavior identical to stock upstream.
    /// </summary>
    public string EmbedderUrl { get; set; }

    /// <summary>Kill-switch for semantic search without clearing the URL.</summary>
    public bool SemanticEnabled { get; set; }

    /// <summary>Sidecar model key (see sidecar /models). Changing it re-embeds stale items automatically.</summary>
    public string EmbedderModel { get; set; }

    /// <summary>
    /// Standalone search gateway base URL (e.g. http://gateway:8100). When set,
    /// movie/series/episode searches are delegated to it first; any gateway
    /// failure falls back to the in-plugin path. Empty = in-plugin only.
    /// </summary>
    public string GatewayUrl { get; set; }
}
