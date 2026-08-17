using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : BasePlugin<Config>, IHasWebPages
{
    private readonly MeilisearchClientHolder _clientHolder;
    private readonly ILogger<Plugin> _logger;
    public readonly Indexer Indexer;
    public long AverageSearchTime;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        MeilisearchClientHolder clientHolder,
        Indexer indexer,
        IHostApplicationLifetime hostApplicationLifetime
    ) : base(
        applicationPaths,
        xmlSerializer)
    {
        _logger = logger;
        _clientHolder = clientHolder;
        Indexer = indexer;
        Instance = this;

        ReloadMeilisearch += (_, _) =>
        {
            logger.LogInformation("Configuration changed, reloading meilisearch...");
            TryCreateMeilisearchClient().Wait();
        };

        hostApplicationLifetime.ApplicationStarted.Register(() => { _ = TryCreateMeilisearchClient(false); });
    }

    private EventHandler<BasePluginConfiguration> ReloadMeilisearch { get; }

    public override string Name => "Bloom";
    public override Guid Id => Guid.Parse("3112ddc0-3e7d-499d-b1b6-5b9e89a6a476");
    public static Plugin? Instance { get; private set; }


    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            // config.html must stay first: the dashboard's plugin "Settings" link
            // resolves to a plugin's first page. Both pages sit in the main menu under
            // distinct names so the diagnostics page no longer shadows the settings one.
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Bloom",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.config.html",
                    GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "search"
            },
            new PluginPageInfo
            {
                Name = "BloomDiagnostics",
                DisplayName = "Bloom Diagnostics",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.semantic.html",
                    GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "insights"
            }
        ];
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = (Config)configuration;
        // Only the Meilisearch connection needs a reload. The search server is
        // external and its provider rebuilds lazily, so changing its URL costs
        // nothing here.
        var skipReload = Configuration.Url == config.Url && Configuration.ApiKey == config.ApiKey;

        Configuration = config;
        SaveConfiguration(Configuration);
        ConfigurationChanged?.Invoke(this, configuration);
        if (!skipReload)
            ReloadMeilisearch.Invoke(this, configuration);
    }

    private volatile Task? _updatingTask;

    public async Task TryCreateMeilisearchClient(bool join = true)
    {
        if (_updatingTask != null)
        {
            _logger.LogWarning("Meilisearch client configuration is still updating，skipping");
            if (join) await _updatingTask;
            return;
        }

        try
        {
            _updatingTask = _TryCreateMeilisearchClient();
            await _updatingTask;
        }
        finally
        {
            _updatingTask = null;
        }
    }

    private async Task _TryCreateMeilisearchClient()
    {
        await _clientHolder.Set(Configuration);
        await Indexer.Index();
    }


    public void UpdateAverageSearchTime(long averageSearchTime)
    {
        lock (this)
        {
            if (AverageSearchTime == 0) AverageSearchTime = averageSearchTime;
            AverageSearchTime = (averageSearchTime + AverageSearchTime) / 2;
        }
    }
}
