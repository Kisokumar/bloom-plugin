using Meilisearch;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Applies a classification (+ query vector) to a Meilisearch SearchQuery.
/// Bm25Only/Passthrough leave the query untouched: stock behavior means the
/// hybrid/vector fields are absent entirely, not zeroed.
/// </summary>
public static class HybridQueryBuilder
{
    public static void Apply(SearchQuery query, QueryClassification classification, float[]? queryVector)
    {
        if (queryVector == null || classification.SemanticRatio is not { } ratio)
            return;

        if (classification.Mode is not QueryMode.SemanticDominant)
            return;

        query.Vector = Array.ConvertAll(queryVector, v => (double)v);
        query.Hybrid = new HybridSearch
        {
            Embedder = SemanticIndexer.EmbedderName,
            SemanticRatio = (float)ratio,
        };
    }
}
