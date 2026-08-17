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
        GatewayUrl = string.Empty;
        GatewayAdminToken = string.Empty;
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
    /// Bloom search server base URL (e.g. http://bloom:8100). When set, movie,
    /// series and episode searches are delegated to it; any failure falls back
    /// to keyword search. Empty = keyword search only, i.e. stock behaviour.
    /// </summary>
    public string GatewayUrl { get; set; }

    /// <summary>
    /// Admin token for the server's /admin API, used to drive enrichment from this
    /// page. Must match the server's ADMIN_TOKEN. Empty = enrichment controls hidden.
    /// The pipeline's own TMDb/LLM keys live in the backend environment, not here.
    /// </summary>
    public string GatewayAdminToken { get; set; }
}
