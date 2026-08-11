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
    EmbedderClientProvider embedderProvider,
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

        object? sidecar = null;
        if (embedderProvider.Get() is var (client, _) && client is not null)
            sidecar = new { healthy = await client.IsHealthyAsync(), model = client.LastModelId };

        return new JsonResult(new
        {
            semanticEnabled = config.SemanticEnabled,
            embedderUrl = config.EmbedderUrl,
            embedderModel = config.EmbedderModel,
            indexStatus = Plugin.Instance.Indexer.Status,
            meili,
            sidecar,
        });
    }

    [HttpGet("debug")]
    public async Task<ActionResult> DebugSearch([FromQuery] string q, [FromQuery] double? ratio = null)
    {
        var index = clientHolder.Index;
        if (index is null || string.IsNullOrWhiteSpace(q))
            return new JsonResult(new { error = "meilisearch not connected or empty query" });

        var (cleaned, facets) = QueryFacetExtractor.Extract(q);
        var semantic = embedderProvider.Get();
        var classification = QueryClassifier.Classify(cleaned, semantic != null);
        if (ratio is { } r && classification.SemanticRatio != null)
            classification = classification with { SemanticRatio = r };

        // Production path first: what the gateway would serve for this query.
        object? gatewayResults = null;
        var gatewayClient = gatewayProvider.Get();
        if (gatewayClient != null)
            gatewayResults = await gatewayClient.ExplainAsync(q, 10, HttpContext.RequestAborted);

        var bm25 = await RunAsync(index, cleaned, facets, null, null);
        object? semanticResults = null;
        if (semantic != null && classification.Mode == QueryMode.SimilarTo)
        {
            semanticResults = await RunSimilarAsync(index, classification.SimilarToTitle!);
        }
        else if (semantic != null && classification.SemanticRatio != null)
        {
            var vector = await semantic.Value.Client.EmbedQueryAsync(cleaned, HttpContext.RequestAborted);
            semanticResults = await RunAsync(index, cleaned, facets, classification, vector);
        }

        return new JsonResult(new
        {
            parsed = new
            {
                original = q,
                cleaned,
                facets,
                mode = classification.Mode.ToString(),
                semanticRatio = classification.SemanticRatio,
                similarToTitle = classification.SimilarToTitle,
            },
            bm25,
            semantic = semanticResults,
            gateway = gatewayResults,
        });
    }

    [HttpGet("models")]
    public async Task<ActionResult> GetModels()
    {
        if (embedderProvider.Get() is not var (client, _) || client is null)
            return new JsonResult(new { models = Array.Empty<object>() });
        var raw = await client.ListModelsRawAsync(HttpContext.RequestAborted);
        return raw == null
            ? new JsonResult(new { models = Array.Empty<object>() })
            : Content(raw, "application/json");
    }

    [HttpPost("backfill")]
    public ActionResult TriggerBackfill()
    {
        _ = Task.Run(() => Plugin.Instance!.Indexer.Index());
        return new JsonResult(new { started = true });
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
        var statsTask = clientHolder.Call(async (_, index) => await index.GetStatsAsync());
        if (statsTask != null)
        {
            try { meiliDocs = (await statsTask).NumberOfDocuments; }
            catch { /* Meili unreachable — meiliDocs stays null */ }
        }

        var baseUrl = config.GatewayUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new JsonResult(new { serverConfigured = false, pluginIndex, meiliDocs });

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
            health,
            stats,
            admin,
        });
    }

    /// <summary>
    /// Connection test for one backend component (gateway/meili/sidecar) used by
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

            case "sidecar":
                if (Coalesce(url, config.EmbedderUrl) is not { } su)
                    return TestResult(target, null, "not configured");
                var (sok, _, serr) = await ProbeAsync(su, "/health", ct);
                return TestResult(target, sok, sok ? "healthy" : serr);

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

    // Enrichment: proxy the gateway's /admin API so the plugin page drives the
    // pipeline. Gateway URL + admin token come from plugin config; the admin's
    // TMDb/LLM keys are injected per run and never persisted on the gateway.
    [HttpGet("enrich/status")]
    public Task<ActionResult> EnrichStatus() => GatewayAdmin(HttpMethod.Get, "/admin/status");

    [HttpGet("enrich/log")]
    public Task<ActionResult> EnrichLog([FromQuery] int offset = 0)
        => GatewayAdmin(HttpMethod.Get, $"/admin/log?offset={offset}");

    [HttpPost("enrich/stop")]
    public Task<ActionResult> EnrichStop() => GatewayAdmin(HttpMethod.Post, "/admin/stop");

    [HttpPost("enrich/run")]
    public Task<ActionResult> EnrichRun([FromBody] JsonElement body)
    {
        var config = Plugin.Instance!.Configuration;
        var keys = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config.TmdbApiKey)) keys["TMDB_API_KEY"] = config.TmdbApiKey;
        if (!string.IsNullOrWhiteSpace(config.LlmApiKey)) keys["ANTHROPIC_API_KEY"] = config.LlmApiKey;
        string? task = body.TryGetProperty("task", out var t) ? t.GetString() : null;
        object? args = body.TryGetProperty("args", out var a) ? a : null;
        return GatewayAdmin(HttpMethod.Post, "/admin/run", new { task, args, keys });
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

    private static async Task<object> RunAsync(
        Index index, string term, IReadOnlyList<string> facets,
        QueryClassification? classification, float[]? vector)
    {
        // Debug view defaults to primary types so Seasons/theme files/collections
        // don't pollute the columns (the real UI filters per row anyway).
        const string primaryTypes = "type IN [\"MediaBrowser.Controller.Entities.Movies.Movie\", " +
            "\"MediaBrowser.Controller.Entities.TV.Series\", \"MediaBrowser.Controller.Entities.TV.Episode\"]";
        var parts = new List<string>(facets);
        if (!facets.Any(f => f.StartsWith("type ", StringComparison.Ordinal)))
            parts.Add(primaryTypes);
        var query = new SearchQuery
        {
            Limit = 10,
            ShowRankingScore = true,
            Filter = string.Join(" AND ", parts),
        };
        if (classification != null)
            HybridQueryBuilder.Apply(query, classification, vector);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await index.SearchAsync<DebugHit>(term, query);
            return new
            {
                ms = sw.ElapsedMilliseconds,
                hits = result.Hits.Select(h => new { h.Name, h.ProductionYear, h.Type, score = h.RankingScore }),
            };
        }
        catch (Exception e)
        {
            return new { error = e.Message };
        }
    }

    private async Task<object> RunSimilarAsync(Index index, string title)
    {
        try
        {
            var resolved = await index.SearchAsync<DebugHit>(title,
                new SearchQuery { Limit = 1, ShowRankingScore = true });
            var anchor = resolved.Hits.FirstOrDefault();
            if (anchor?.Guid == null || anchor.RankingScore < 0.4)
                return new { error = $"could not resolve '{title}'; real search falls back to semantic on the raw query" };

            var similar = await index.SearchSimilarDocumentsAsync<DebugHit>(
                new SimilarDocumentsQuery(anchor.Guid)
                {
                    Embedder = SemanticIndexer.EmbedderName,
                    Limit = 30,
                    ShowRankingScore = true,
                });

            var hits = similar.Hits
                .DistinctBy(h => (h.Name?.ToLowerInvariant(), h.ProductionYear))
                .OrderBy(h => FranchiseMatcher.IsSameFranchise(anchor.Name, h.Name) ? 1 : 0)
                .ThenByDescending(h => SimilarityRescorer.Blend(
                    h.RankingScore ?? 0, h.CommunityRating, h.CriticRating))
                .Take(10)
                .Select(h => new
                {
                    h.Name,
                    h.ProductionYear,
                    h.Type,
                    score = SimilarityRescorer.Blend(h.RankingScore ?? 0, h.CommunityRating, h.CriticRating),
                });
            return new { anchor = $"{anchor.Name} ({anchor.ProductionYear})", hits };
        }
        catch (Exception e)
        {
            return new { error = e.Message };
        }
    }

    private sealed record DebugHit(
        string? Name, int? ProductionYear, string? Type, string? Guid,
        double? CommunityRating, double? CriticRating,
        [property: System.Text.Json.Serialization.JsonPropertyName("_rankingScore")] double? RankingScore);
}
