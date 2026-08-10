namespace Jellyfin.Plugin.Meilisearch.Semantic;

public enum QueryMode
{
    /// <summary>Empty query: let the caller pass through untouched.</summary>
    Passthrough,

    /// <summary>No semantic capability (embedder unset/unhealthy): stock BM25.</summary>
    Bm25Only,

    /// <summary>Title-ish query: hybrid with a low semantic ratio.</summary>
    Bm25Dominant,

    /// <summary>Vibe query: hybrid with a high semantic ratio.</summary>
    SemanticDominant,

    /// <summary>"like &lt;X&gt;": resolve X, then ANN on X's stored vector.</summary>
    SimilarTo,
}

public sealed record QueryClassification(QueryMode Mode, double? SemanticRatio, string? SimilarToTitle = null)
{
    public static readonly QueryClassification Passthrough = new(QueryMode.Passthrough, null);
    public static readonly QueryClassification Bm25Only = new(QueryMode.Bm25Only, null);

    /// <summary>
    /// Title-shaped → pure BM25 (no ratio): Meili's typo tolerance already covers
    /// misspellings, and any hybrid blend fills the page with lookalike filler.
    /// Meili's hybrid _rankingScore is not a linear blend, so a score threshold
    /// can't cut that tail; staying stock is the correct behavior here.
    /// </summary>
    public static QueryClassification Bm25Dominant() => new(QueryMode.Bm25Dominant, null);

    public static QueryClassification SemanticDominant() => new(QueryMode.SemanticDominant, 0.7);
    public static QueryClassification SimilarTo(string title) => new(QueryMode.SimilarTo, 1.0, title);
}
