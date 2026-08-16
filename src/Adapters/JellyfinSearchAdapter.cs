using System.Text.Json.Serialization;
using Jellyfin.Plugin.Meilisearch.Semantic;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>
/// The decorator's single entry point into semantic search:
/// classify → (embed) → hybrid or similar-to → ordered ids + total.
/// With no embedder configured this produces byte-identical requests to
/// stock 1.11.1.15 (no hybrid/vector fields at all).
/// </summary>
public class JellyfinSearchAdapter(
    EmbedderClientProvider embedderProvider,
    GatewayClientProvider gatewayProvider,
    SearchAnalytics analytics,
    ILogger<JellyfinSearchAdapter> logger)
{
    private const double SimilarToResolveThreshold = 0.4;

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
        // Gateway-first: the standalone gateway owns classification, enrichment
        // phrases and fusion for the Movie/Series/Episode leg. Raw term goes
        // through untouched (it extracts its own facets). Real clients search
        // mixed types in one call, so the handoff is per-leg: gateway ranks the
        // media leg, plain keyword covers the rest (music, people, …). Paging
        // is served by overfetching offset+limit and slicing. Gateway errors
        // fall through wholesale to the in-plugin path.
        var gateway = gatewayProvider.Get();
        if (gateway != null)
        {
            var (gatewayTypes, otherTypes) = GatewayClient.SplitTypes(typeFilter);
            if (gatewayTypes.Count > 0)
            {
                var gatewayIds = await gateway
                    .SearchAsync(searchTerm, gatewayTypes, offset + limit, cancellationToken)
                    .ConfigureAwait(false);
                if (gatewayIds != null)
                {
                    var page = gatewayIds.Skip(offset).Take(limit).ToList();
                    var exhausted = gatewayIds.Count < offset + limit;
                    var gatewayTotal = exhausted ? gatewayIds.Count : offset + limit + limit;
                    if (otherTypes.Count > 0 && offset == 0)
                    {
                        var rest = await KeywordRestAsync(
                            index, searchTerm, otherTypes, limit, matchingStrategy, cancellationToken)
                            .ConfigureAwait(false);
                        page = page.Concat(rest.Where(id => !page.Contains(id))).ToList();
                        gatewayTotal = Math.Max(gatewayTotal, page.Count);
                    }

                    // An empty page at offset 0 falls through to the in-plugin
                    // safety net; an empty page beyond that is the genuine end
                    // of results (falling through would resurface junk).
                    if (page.Count > 0 || offset > 0)
                        return ((page, Math.Max(gatewayTotal, page.Count)), "Gateway");
                }
            }
        }

        var (cleanedTerm, facetFilters) = QueryFacetExtractor.Extract(searchTerm);
        if (facetFilters.Count > 0)
        {
            searchTerm = cleanedTerm;
            typeFilter = $"({typeFilter}) AND {string.Join(" AND ", facetFilters)}";
        }

        var semantic = embedderProvider.Get();
        var classification = QueryClassifier.Classify(searchTerm, semantic != null);

        // A query that carried facets ("comedies under 90 minutes") is browse
        // intent even if the leftover is one token; keyword can't stem it.
        if (facetFilters.Count > 0 && classification.Mode == QueryMode.Bm25Dominant && semantic != null)
            classification = QueryClassification.SemanticDominant();

        string? similarToTitle = null;
        if (classification.Mode == QueryMode.SimilarTo && semantic != null)
        {
            var similar = await TrySimilarToAsync(
                index, classification.SimilarToTitle!, typeFilter, offset, limit, cancellationToken)
                .ConfigureAwait(false);
            if (similar != null)
                return (similar.Value, "SimilarTo");

            // Unresolvable title → treat the raw query as a vibe search, but
            // remember the extracted title for the keyword-only last resort.
            similarToTitle = classification.SimilarToTitle;
            classification = QueryClassification.SemanticDominant();
        }

        var query = new SearchQuery
        {
            Filter = typeFilter,
            Offset = offset,
            Limit = limit,
            MatchingStrategy = matchingStrategy,
        };

        if (semantic != null && classification.SemanticRatio is not null)
        {
            var vector = await semantic.Value.Client
                .EmbedQueryAsync(searchTerm, cancellationToken).ConfigureAwait(false);
            if (vector == null)
                logger.LogWarning("Query embedding unavailable, '{Term}' degrades to BM25", searchTerm);
            HybridQueryBuilder.Apply(query, classification, vector);
        }

        ISearchable<MeilisearchItem> result;
        try
        {
            result = await index.SearchAsync<MeilisearchItem>(searchTerm, query, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (query.Hybrid != null && e is not OperationCanceledException)
        {
            // Meili rejects hybrid queries while the embedder isn't (yet)
            // configured, e.g. mid-backfill on a fresh index. Degrade this
            // query to stock BM25 rather than surfacing an empty result. For
            // "movies like X" the extracted title beats the literal phrase,
            // which would keyword-match every "Like ..." in the library.
            var fallbackTerm = similarToTitle ?? searchTerm;
            logger.LogWarning(e, "Hybrid search failed for '{Term}', retrying as pure BM25 on '{Fallback}'",
                searchTerm, fallbackTerm);
            query.Vector = null;
            query.Hybrid = null;
            result = await index.SearchAsync<MeilisearchItem>(fallbackTerm, query, cancellationToken)
                .ConfigureAwait(false);
        }

        var ids = ParseIds(result.Hits.Select(h => h.Guid));
        var total = result is SearchResult<MeilisearchItem> sr ? sr.EstimatedTotalHits : ids.Count;
        return ((ids, total), classification.Mode.ToString());
    }

    private async Task<(IReadOnlyList<Guid>, int)?> TrySimilarToAsync(
        Index index, string title, string typeFilter, int offset, int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await index.SearchAsync<ScoredHit>(title, new SearchQuery
            {
                Filter = typeFilter,
                Limit = 1,
                ShowRankingScore = true,
            }, cancellationToken).ConfigureAwait(false);

            var anchor = resolved.Hits.FirstOrDefault();
            if (anchor?.Guid == null || anchor.RankingScore < SimilarToResolveThreshold)
            {
                logger.LogInformation(
                    "SimilarTo: could not resolve '{Title}' (score {Score}), falling back to semantic search",
                    title, anchor?.RankingScore ?? 0);
                return null;
            }

            // Over-fetch a pool: raw vector similarity is topical but tasteless,
            // so rescore with the audience rating (canon rises, junk sinks),
            // dedupe library duplicates, then demote (never exclude)
            // franchise-mates ("like X" means discovery, not X's sequels).
            var similar = await index.SearchSimilarDocumentsAsync<SimilarHit>(
                new SimilarDocumentsQuery(anchor.Guid)
                {
                    Embedder = SemanticIndexer.EmbedderName,
                    Filter = typeFilter,
                    Offset = offset,
                    Limit = Math.Max(50, limit + 10),
                    ShowRankingScore = true,
                }, cancellationToken).ConfigureAwait(false);

            var ordered = similar.Hits
                .DistinctBy(h => (h.Name?.ToLowerInvariant(), h.ProductionYear))
                .OrderBy(h => FranchiseMatcher.IsSameFranchise(anchor.Name, h.Name) ? 1 : 0)
                .ThenByDescending(h => SimilarityRescorer.Blend(h.RankingScore, h.CommunityRating, h.CriticRating))
                .Take(limit);
            var ids = ParseIds(ordered.Select(h => h.Guid));
            return ids.Count == 0 ? null : (ids, ids.Count + offset);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "SimilarTo('{Title}') failed, falling back", title);
            return null;
        }
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
            logger.LogWarning(e, "Keyword leg for non-media types failed, gateway leg only");
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

    private sealed record SimilarHit(
        [property: JsonPropertyName("guid")] string? Guid,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("productionYear")] int? ProductionYear,
        [property: JsonPropertyName("communityRating")] double? CommunityRating,
        [property: JsonPropertyName("criticRating")] double? CriticRating,
        [property: JsonPropertyName("_rankingScore")] double RankingScore);
}
