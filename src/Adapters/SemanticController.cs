using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Meilisearch.Semantic;
using MediaBrowser.Common.Api;
using Meilisearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>Admin API for the Semantic dashboard page. Separate controller = zero upstream diff.</summary>
[Route("meilisearch/semantic")]
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
public class SemanticController(
    MeilisearchClientHolder clientHolder,
    GatewayClientProvider gatewayProvider,
    SearchAnalytics analytics) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        object? meili = null;
        var statsTask = clientHolder.Call(async (_, index) => await index.GetStatsAsync());
        if (statsTask != null)
        {
            var s = await statsTask;
            meili = new
            {
                documents = s.NumberOfDocuments,
                // embedded-docs count isn't surfaced by SDK 0.18; coverage ≈ docs when healthy
                indexing = s.IsIndexing,
            };
        }

        // MEILI_URL/MEILI_MASTER_KEY in the environment take priority over these
        // fields, so report when that is happening -- otherwise the settings box
        // looks editable while silently having no effect.
        var envUrl = Environment.GetEnvironmentVariable("MEILI_URL");
        var envKey = Environment.GetEnvironmentVariable("MEILI_MASTER_KEY");

        return new JsonResult(new
        {
            indexStatus = Plugin.Instance.Indexer.Status,
            meili,
            meiliUrlFromEnv = string.IsNullOrEmpty(envUrl) ? null : envUrl,
            meiliKeyFromEnv = !string.IsNullOrEmpty(envKey),
        });
    }


    /// <summary>
    /// The search server's own explanation of a query, for the diagnostics page.
    /// A pass-through: the plugin has no reasoning of its own to show.
    /// </summary>
    [HttpGet("debug")]
    public async Task<ActionResult> DebugSearch([FromQuery] string q)
    {
        var server = gatewayProvider.Get();
        if (server is null)
            return new JsonResult(new { error = "no search server configured" });
        if (string.IsNullOrWhiteSpace(q))
            return new JsonResult(new { error = "empty query" });
        var trace = await server.ExplainAsync(q, 10, HttpContext.RequestAborted);
        return new JsonResult(new { gateway = trace });
    }

    [HttpGet("analytics")]
    public ActionResult GetAnalytics() => new JsonResult(analytics.Summarize());

    /// <summary>
    /// One-shot readiness roll-up for the config page: server health + the index it
    /// advertises, corpus coverage, key presence, and the plugin's own Meili view.
    /// The admin token never leaves the server; /admin/status reports key presence
    /// (set/unset + fingerprint), never values. Every leg fails soft to null so the
    /// page can render a partial picture rather than error.
    /// </summary>
    [HttpGet("readiness")]
    public async Task<ActionResult> Readiness()
    {
        var config = Plugin.Instance!.Configuration;
        var ct = HttpContext.RequestAborted;

        var pluginIndex = clientHolder.Index?.Uid;
        long? meiliDocs = null;

        // Unreachable and empty need telling apart. Both used to arrive as a null
        // document count, so the page advised rebuilding the index against a host
        // it could not reach, which can only fail.
        var meiliReachable = false;
        var statsTask = clientHolder.Call(async (_, index) => await index.GetStatsAsync());
        if (statsTask != null)
        {
            try
            {
                meiliDocs = (await statsTask).NumberOfDocuments;
                meiliReachable = true;
            }
            catch { /* stays unreachable; the holder logs the transport error */ }
        }

        var meiliUrl = Environment.GetEnvironmentVariable("MEILI_URL") is { Length: > 0 } env
            ? env
            : config.Url;

        var baseUrl = config.GatewayUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new JsonResult(new
            {
                serverConfigured = false, pluginIndex, meiliDocs, meiliReachable, meiliUrl,
            });

        var health = await GetServerJsonAsync(baseUrl, "/health", null, ct);
        var stats = await GetServerJsonAsync(baseUrl, "/stats", null, ct);
        var admin = string.IsNullOrWhiteSpace(config.GatewayAdminToken) ? null
            : await GetServerJsonAsync(baseUrl, "/admin/status", config.GatewayAdminToken, ct);

        return new JsonResult(new
        {
            serverConfigured = true,
            serverReachable = health != null,
            adminTokenSet = !string.IsNullOrWhiteSpace(config.GatewayAdminToken),
            pluginIndex,
            meiliDocs,
            meiliReachable,
            meiliUrl,
            health,
            stats,
            admin,
        });
    }

    /// <summary>
    /// Connection test for one backend component (server/meili) used by
    /// the config page. An explicit `url` overrides the saved value, so a typed
    /// URL can be tested before saving. Always returns {ok, detail}; never throws.
    /// </summary>
    [HttpGet("test")]
    public async Task<ActionResult> Test([FromQuery] string target, [FromQuery] string? url = null)
    {
        var config = Plugin.Instance!.Configuration;
        var ct = HttpContext.RequestAborted;
        switch (target)
        {
            case "gateway":
                if (Coalesce(url, config.GatewayUrl) is not { } gw)
                    return TestResult(target, null, "not configured");
                return await TestGatewayAsync(gw, ct);

            case "meili":
                if (Coalesce(url, config.Url) is not { } mu)
                    return TestResult(target, null, "not configured");
                var (mok, mbody, merr) = await ProbeAsync(mu, "/health", ct);
                var mstatus = TryStr(mbody, "status");
                return TestResult(target, mok && mstatus != "unavailable", mok ? mstatus ?? "reachable" : merr);


            default:
                return TestResult(target, false, "unknown target");
        }
    }

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<ActionResult> TestGatewayAsync(string baseUrl, CancellationToken ct)
    {
        var (ok, body, err) = await ProbeAsync(baseUrl, "/health", ct);
        if (!ok)
            return TestResult("gateway", false, err ?? "unreachable");
        var status = TryStr(body, "status") ?? "ok";
        var down = new[] { "meili", "qdrant", "sidecar" }
            .Where(c => body is { } b && b.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.False)
            .ToList();
        var detail = status;
        var (statsOk, stats, _) = await ProbeAsync(baseUrl, "/stats", ct);
        if (statsOk && stats is { } s && s.TryGetProperty("enriched", out var en) && en.TryGetInt32(out var n))
            detail += $" · {n} enriched";
        if (down.Count > 0)
            detail += " · down: " + string.Join(",", down);
        return TestResult("gateway", status != "down" && down.Count == 0, detail);
    }

    private static async Task<(bool Ok, JsonElement? Body, string? Error)> ProbeAsync(
        string baseUrl, string path, CancellationToken ct)
    {
        try
        {
            using var resp = await Probe.GetAsync(baseUrl.TrimEnd('/') + path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)resp.StatusCode}");
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return (true, doc.RootElement.Clone(), null);
            }
            catch (JsonException)
            {
                return (true, null, null); // 200 with non-JSON body still counts as reachable
            }
        }
        catch (Exception e)
        {
            return (false, null, e is TaskCanceledException ? "timeout" : e.Message);
        }
    }

    private static async Task<JsonElement?> GetServerJsonAsync(
        string baseUrl, string path, string? adminToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            if (adminToken != null) req.Headers.Add("X-Admin-Token", adminToken);
            using var resp = await Probe.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static string? Coalesce(string? typed, string? saved)
        => !string.IsNullOrWhiteSpace(typed) ? typed.Trim()
            : !string.IsNullOrWhiteSpace(saved) ? saved.Trim() : null;

    private static string? TryStr(JsonElement? body, string prop)
        => body is { } b && b.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // ok=null => not configured (grey in the UI); the UI keys the badge off `configured`.
    private static JsonResult TestResult(string target, bool? ok, string? detail)
        => new(new { target, ok = ok ?? false, configured = ok != null, detail });

    // Enrichment: proxy the server's /admin API so the plugin page drives the
    // pipeline. Only the server URL and admin token come from plugin config; the
    // LLM key lives in the server's own environment and never passes through here.
    [HttpGet("enrich/status")]
    public Task<ActionResult> EnrichStatus() => GatewayAdmin(HttpMethod.Get, "/admin/status");

    [HttpGet("enrich/embedders")]
    public Task<ActionResult> EnrichEmbedders() => GatewayAdmin(HttpMethod.Get, "/admin/embedders");

    /// <summary>
    /// The Meilisearch coordinates the server reads from, so the plugin can index
    /// into the same instance instead of having them typed twice. The server is
    /// authoritative; this is what makes "one source of truth" true.
    /// </summary>
    [HttpGet("server-meili")]
    public Task<ActionResult> ServerMeili() => GatewayAdmin(HttpMethod.Get, "/admin/meili");

    [HttpGet("enrich/log")]
    public Task<ActionResult> EnrichLog([FromQuery] int offset = 0)
        => GatewayAdmin(HttpMethod.Get, $"/admin/log?offset={offset}");

    [HttpPost("enrich/stop")]
    public Task<ActionResult> EnrichStop() => GatewayAdmin(HttpMethod.Post, "/admin/stop");

    [HttpPost("enrich/run")]
    public Task<ActionResult> EnrichRun([FromBody] JsonElement body)
    {
        // The pipeline reads its TMDb/LLM keys from the backend environment; the
        // plugin only names the task to run.
        string? task = body.TryGetProperty("task", out var t) ? t.GetString() : null;
        object? args = body.TryGetProperty("args", out var a) ? a : null;
        return GatewayAdmin(HttpMethod.Post, "/admin/run", new { task, args });
    }

    private async Task<ActionResult> GatewayAdmin(HttpMethod method, string path, object? body = null)
    {
        var config = Plugin.Instance!.Configuration;
        var gw = config.GatewayUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(gw) || string.IsNullOrWhiteSpace(config.GatewayAdminToken))
            return new JsonResult(new { error = "set the Gateway URL and admin token first" }) { StatusCode = 400 };
        using var req = new HttpRequestMessage(method, gw + path);
        req.Headers.Add("X-Admin-Token", config.GatewayAdminToken);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var resp = await Probe.SendAsync(req, HttpContext.RequestAborted).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return new ContentResult { Content = text, ContentType = "application/json", StatusCode = (int)resp.StatusCode };
        }
        catch (Exception e)
        {
            return new JsonResult(new { error = e.Message }) { StatusCode = 502 };
        }
    }

}
