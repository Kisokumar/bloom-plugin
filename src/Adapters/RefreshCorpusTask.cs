using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// Keeps the search corpus level with the library: reads new titles from
/// Jellyfin and embeds whatever has no vectors yet.
///
/// Free and idempotent by construction. The server's embed step only selects
/// titles missing from vector_state, and this task never *submits* to an LLM --
/// that is what costs money, so it stays a deliberate action on the settings page.
///
/// It will collect a batch that was already paid for. Otherwise a batch that
/// finishes overnight sits uncollected until someone happens to open the
/// settings page, and the descriptions the user paid for stay invisible.
/// </summary>
public class RefreshCorpusTask(ILogger<RefreshCorpusTask> logger) : IScheduledTask
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public string Name => "Refresh Bloom corpus";
    public string Key => "task-bloom-refresh-corpus";
    public string Category => "Bloom";

    public string Description =>
        "Reads new titles from your library, builds their search vectors, and collects " +
        "any LLM batch you have already paid for. Free, and safe to run as often as " +
        "you like: each step only does outstanding work, so nothing already indexed " +
        "is redone. It never sends anything new to an LLM. That is the part that " +
        "costs money, and it stays a manual action on the Bloom settings page, which " +
        "shows how many titles are waiting and what they would cost.";

    // Nightly by default: new media should become searchable without anyone
    // remembering to press a button.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var server = config?.GatewayUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(config!.GatewayAdminToken))
        {
            // Nothing to do rather than a nightly failure: plenty of installs run
            // keyword-only, and a red task they cannot fix is just noise.
            logger.LogDebug("No Bloom server or admin token configured, skipping corpus refresh");
            progress.Report(100);
            return;
        }

        // Poll before embed, so descriptions collected this run become searchable in
        // the same pass rather than waiting another night.
        var steps = await BatchPendingAsync(server, config.GatewayAdminToken, cancellationToken)
            .ConfigureAwait(false)
            ? new[] { "pull", "distil-poll", "embed" }
            : new[] { "pull", "embed" };
        for (var i = 0; i < steps.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunAsync(server, config.GatewayAdminToken, steps[i], cancellationToken).ConfigureAwait(false);
            await WaitForIdleAsync(server, config.GatewayAdminToken, cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / steps.Length);
        }
    }

    private async Task RunAsync(string server, string token, string task, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, server + "/admin/run");
        req.Headers.Add("X-Admin-Token", token);
        req.Content = new StringContent(JsonSerializer.Serialize(new { task }), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Bloom server rejected '{task}': HTTP {(int)resp.StatusCode}");
        logger.LogInformation("Bloom corpus refresh: started {Task}", task);
    }

    /// <summary>A submitted batch the user has paid for but not yet collected.</summary>
    private async Task<bool> BatchPendingAsync(string server, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, server + "/admin/status");
            req.Headers.Add("X-Admin-Token", token);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            return body.TryGetProperty("batch_pending", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            // An older server has no batch_pending; refreshing the corpus still works.
            logger.LogDebug(ex, "Could not determine whether a Bloom batch is pending");
            return false;
        }
    }

    /// <summary>The server runs one job at a time, so the next step has to wait.</summary>
    private async Task WaitForIdleAsync(string server, string token, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, server + "/admin/status");
            req.Headers.Add("X-Admin-Token", token);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            if (!body.TryGetProperty("job", out var job)
                || !job.TryGetProperty("running", out var running)
                || running.ValueKind != JsonValueKind.True)
                return;
        }
    }
}
