using Jellyfin.Plugin.Meilisearch.Semantic;
using Meilisearch;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class SemanticBuildersTests
{
    private static MeilisearchItem Item(string name = "Superbad") => new(
        Guid: "abc", Type: "MediaBrowser.Controller.Entities.Movies.Movie", ParentId: null,
        Name: name, Overview: "Two seniors chase a party.", OriginalTitle: null, SeriesName: null,
        ProductionYear: 2007, Artists: null, AlbumArtists: null,
        Genres: ["Comedy"], Studios: null, Tags: ["teen comedy", "one crazy night"],
        IsFolder: false, CommunityRating: null, CriticRating: null, Path: null,
        Tagline: "Come and get some.", SortName: null,
        People: ["Jonah Hill", "Michael Cera"], OfficialRating: "R", Decade: "2000s");

    [Fact]
    public void Embed_text_follows_plan_template()
    {
        var text = IndexDocumentBuilder.BuildEmbedText(Item());
        Assert.Equal(
            "Comedy. teen comedy, one crazy night. Jonah Hill, Michael Cera. Two seniors chase a party. Come and get some. Superbad, Movie 2007. 2000s. Rated R.",
            text);
    }

    [Fact]
    public void Embed_hash_is_stable_and_content_and_model_sensitive()
    {
        var a = IndexDocumentBuilder.ComputeEmbedHash("x", "m1");
        Assert.Equal(a, IndexDocumentBuilder.ComputeEmbedHash("x", "m1"));
        Assert.Equal(16, a.Length);
        Assert.NotEqual(a, IndexDocumentBuilder.ComputeEmbedHash("y", "m1"));
        Assert.NotEqual(a, IndexDocumentBuilder.ComputeEmbedHash("x", "m2"));
    }

    [Fact]
    public void Null_vector_leaves_query_stock()
    {
        var query = new SearchQuery { Limit = 30 };
        HybridQueryBuilder.Apply(query, QueryClassification.SemanticDominant(), null);
        Assert.Null(query.Vector);
        Assert.Null(query.Hybrid);
    }

    [Fact]
    public void Bm25_only_leaves_query_stock_even_with_vector()
    {
        var query = new SearchQuery();
        HybridQueryBuilder.Apply(query, QueryClassification.Bm25Only, new float[384]);
        Assert.Null(query.Hybrid);
    }

    [Fact]
    public void Semantic_dominant_sets_hybrid_params_and_keeps_full_tail()
    {
        var query = new SearchQuery();
        HybridQueryBuilder.Apply(query, QueryClassification.SemanticDominant(), new float[384]);
        Assert.NotNull(query.Hybrid);
        Assert.Equal(0.7f, query.Hybrid!.SemanticRatio);
        Assert.Equal("default", query.Hybrid.Embedder);
        Assert.Equal(384, query.Vector!.Count());
        Assert.Null(query.RankingScoreThreshold);
    }

    [Fact]
    public void Bm25_dominant_is_pure_stock_bm25()
    {
        var query = new SearchQuery();
        HybridQueryBuilder.Apply(query, QueryClassification.Bm25Dominant(), new float[384]);
        Assert.Null(query.Hybrid);
        Assert.Null(query.Vector);
        Assert.Null(query.RankingScoreThreshold);
    }
}
