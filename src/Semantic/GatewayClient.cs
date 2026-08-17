using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Client for the standalone search gateway (enrichment pipeline v2). When
/// configured, whole-query retrieval is delegated to it: intent classification,
/// vector fusion and enrichment phrases all live server-side. Every failure mode
/// (unreachable, non-200, bad payload) returns null so the caller can fall back
/// to plain keyword search; search must never break because the server is
/// down.
/// </summary>
public partial class GatewayClient(HttpClient http, string baseUrl, ILogger<GatewayClient> logger)
{
    private static readonly HashSet<string> SupportedTypes = ["Movie", "Series", "Episode"];
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    [GeneratedRegex("type = \"([^\"]+)\"")]
    private static partial Regex TypeClauseRegex();

    /// <summary>
    /// Splits the Meili type filter ('type = "...Movies.Movie" OR ...') into
    /// gateway-supported type names and the remaining qualified types. Real
    /// clients search mixed types in one call (Movie,MusicAlbum,Person,…), so
    /// the handoff is per-leg: gateway ranks its media leg, keyword covers the
    /// rest.
    /// </summary>
    public static (IReadOnlyList<string> Supported, IReadOnlyList<string> Unsupported)
        SplitTypes(string? typeFilter)
    {
        List<string> supported = [], unsupported = [];
        if (string.IsNullOrWhiteSpace(typeFilter))
            return (supported, unsupported);
        foreach (Match m in TypeClauseRegex().Matches(typeFilter))
        {
            var qualified = m.Groups[1].Value;
            var name = qualified[(qualified.LastIndexOf('.') + 1)..];
            if (SupportedTypes.Contains(name))
                supported.Add(name);
            else
                unsupported.Add(qualified);
        }

        return (supported, unsupported);
    }

    /// <summary>Raw /explain passthrough for the admin debug page. Null on any failure.</summary>
    public async Task<JsonElement?> ExplainAsync(string term, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/explain?q={Uri.EscapeDataString(term)}&limit={limit}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Gateway explain failed for '{Term}'", term);
            return null;
        }
    }

    public async Task<IReadOnlyList<Guid>?> SearchAsync(
        string term, IReadOnlyList<string> types, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/search?q={Uri.EscapeDataString(term)}&limit={limit}"
                      + $"&types={string.Join(",", types)}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gateway returned {Status} for '{Term}', using in-plugin path",
                    (int)response.StatusCode, term);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseIds(body);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes HttpClient timeouts (TaskCanceledException without caller cancellation).
            logger.LogWarning(e, "Gateway search failed for '{Term}', using in-plugin path", term);
            return null;
        }
    }

    public static List<Guid>? ParseIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return null;
            List<Guid> ids = [];
            foreach (var hit in results.EnumerateArray())
            {
                if (hit.TryGetProperty("id", out var idEl)
                    && Guid.TryParse(idEl.GetString(), out var id))
                    ids.Add(id);
            }

            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
