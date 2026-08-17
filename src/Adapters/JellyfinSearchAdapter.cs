using System.Text.Json.Serialization;
using Jellyfin.Plugin.Meilisearch.Semantic;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// The decorator's entry point: hand the query to the Bloom server, or answer
/// it with plain Meilisearch keyword search.
///
/// The plugin owns no ranking of its own. Classification, enrichment, fusion
/// and reranking live on the server, where they can change without a Jellyfin
/// deploy and only have to exist once. Without a server this is stock keyword
/// behaviour, which is also the fallback whenever the server cannot answer.
/// </summary>
public class JellyfinSearchAdapter(
    GatewayClientProvider gatewayProvider,
    SearchAnalytics analytics,
    ILogger<JellyfinSearchAdapter> logger)
{
    public async Task<(IReadOnlyList<Guid> Ids, int Total)> SearchAsync(
        Index index,
        string searchTerm,
        string typeFilter,
        int offset,
        int limit,
        string matchingStrategy,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (result, mode) = await SearchCoreAsync(
            index, searchTerm, typeFilter, offset, limit, matchingStrategy, cancellationToken)
            .ConfigureAwait(false);
        analytics.Record(searchTerm, mode, result.Ids.Count, sw.ElapsedMilliseconds);
        return result;
    }

    private async Task<((IReadOnlyList<Guid> Ids, int Total), string Mode)> SearchCoreAsync(
        Index index,
        string searchTerm,
        string typeFilter,
        int offset,
        int limit,
        string matchingStrategy,
        CancellationToken cancellationToken)
    {
        // Server-first. The raw term goes through untouched -- the server
        // extracts its own facets. Real clients search mixed types in one call,
        // so the handoff is per-leg: the server ranks the media leg and keyword
        // covers the rest (music, people, ...). Paging overfetches and slices.
        var server = gatewayProvider.Get();
        if (server != null)
        {
            var (serverTypes, otherTypes) = GatewayClient.SplitTypes(string.Join(",", TypesFromFilter(typeFilter)));
            if (serverTypes.Count > 0)
            {
                var serverIds = await server
                    .SearchAsync(searchTerm, serverTypes, offset + limit, cancellationToken)
                    .ConfigureAwait(false);
                if (serverIds != null)
                {
                    var page = serverIds.Skip(offset).Take(limit).ToList();
                    var exhausted = serverIds.Count < offset + limit;
                    var serverTotal = exhausted ? serverIds.Count : offset + limit + limit;

                    if (otherTypes.Count > 0 && page.Count < limit)
                    {
                        var rest = await KeywordRestAsync(index, searchTerm, otherTypes,
                            limit - page.Count, matchingStrategy, cancellationToken).ConfigureAwait(false);
                        page.AddRange(rest.Where(id => !page.Contains(id)));
                        serverTotal = Math.Max(serverTotal, page.Count);
                    }

                    // An empty first page means the server had nothing useful,
                    // so keyword gets a turn. An empty page further in is the
                    // genuine end of results -- falling through there would
                    // resurface junk the server already rejected.
                    if (page.Count > 0 || offset > 0)
                        return ((page, Math.Max(serverTotal, page.Count)), "Server");
                }
            }
        }

        var query = new SearchQuery
        {
            Filter = typeFilter,
            Offset = offset,
            Limit = limit,
            MatchingStrategy = matchingStrategy,
        };

        try
        {
            var result = await index.SearchAsync<ScoredHit>(searchTerm, query, cancellationToken)
                .ConfigureAwait(false);
            var ids = ParseIds(result.Hits.Select(h => h.Guid));
            return ((ids, ids.Count + offset), "Keyword");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never surface a search error to the client: an empty result lets
            // the decorator fall through to Jellyfin's own search.
            logger.LogWarning(e, "Keyword search failed for '{Term}'", searchTerm);
            return ((Array.Empty<Guid>(), 0), "Failed");
        }
    }

    /// <summary>Meili filter string back to the qualified type names the server speaks.</summary>
    private static List<string> TypesFromFilter(string typeFilter)
    {
        List<string> types = [];
        foreach (var part in typeFilter.Split(" OR ", StringSplitOptions.TrimEntries))
        {
            var open = part.IndexOf('"');
            var close = part.LastIndexOf('"');
            if (open >= 0 && close > open)
                types.Add(part[(open + 1)..close]);
        }

        return types;
    }

    private async Task<List<Guid>> KeywordRestAsync(
        Index index, string term, IReadOnlyList<string> qualifiedTypes, int limit,
        string matchingStrategy, CancellationToken cancellationToken)
    {
        try
        {
            var filter = string.Join(" OR ", qualifiedTypes.Select(t => $"type = \"{t}\""));
            var result = await index.SearchAsync<ScoredHit>(term, new SearchQuery
            {
                Filter = filter,
                Limit = limit,
                MatchingStrategy = matchingStrategy,
            }, cancellationToken).ConfigureAwait(false);
            return ParseIds(result.Hits.Select(h => h.Guid));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Keyword leg for non-media types failed, server leg only");
            return [];
        }
    }

    private List<Guid> ParseIds(IEnumerable<string?> rawGuids)
    {
        List<Guid> ids = [];
        foreach (var raw in rawGuids)
        {
            if (Guid.TryParse(raw, out var id))
                ids.Add(id);
            else
                logger.LogWarning("Skipping Meilisearch hit with invalid GUID '{Guid}'", raw);
        }

        return ids;
    }

    private sealed record ScoredHit(
        [property: JsonPropertyName("guid")] string? Guid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("_rankingScore")] double RankingScore);
}
