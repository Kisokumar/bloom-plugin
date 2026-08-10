using Jellyfin.Plugin.Meilisearch.Semantic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// Hands out a GatewayClient for the currently configured GatewayUrl,
/// rebuilding when the URL changes. Null when no gateway is configured or
/// semantic search is disabled; callers treat that as "in-plugin path only".
/// </summary>
public class GatewayClientProvider(ILoggerFactory loggerFactory)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private GatewayClient? _client;
    private string? _url;

    public GatewayClient? Get()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { SemanticEnabled: true } || string.IsNullOrWhiteSpace(config.GatewayUrl))
            return null;

        lock (_lock)
        {
            if (_client == null || _url != config.GatewayUrl)
            {
                _url = config.GatewayUrl;
                _client = new GatewayClient(Http, config.GatewayUrl,
                    loggerFactory.CreateLogger<GatewayClient>());
            }

            return _client;
        }
    }
}
