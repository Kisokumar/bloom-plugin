using Jellyfin.Plugin.Meilisearch.Semantic;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class QueryClassifierTests
{
    [Theory]
    // mission checks
    [InlineData("feel good movies", QueryMode.SemanticDominant)]
    [InlineData("horror but funny", QueryMode.SemanticDominant)]
    [InlineData("breaking bad", QueryMode.Bm25Dominant)]
    // classification boundaries
    [InlineData("superbad", QueryMode.Bm25Dominant)]
    [InlineData("the grand budapest hotel", QueryMode.SemanticDominant)]
    [InlineData("movies about grief", QueryMode.SemanticDominant)]
    [InlineData("\"the office\"", QueryMode.Bm25Dominant)]
    [InlineData("'feel good movies'", QueryMode.Bm25Dominant)]
    // trailing media noun = browse intent, even at 1-2 tokens
    [InlineData("scary movies", QueryMode.SemanticDominant)]
    [InlineData("heist films", QueryMode.SemanticDominant)]
    [InlineData("paddington 2", QueryMode.Bm25Dominant)]
    public void Classifies_mode(string query, QueryMode expected)
    {
        var c = QueryClassifier.Classify(query, semanticAvailable: true);
        Assert.Equal(expected, c.Mode);
    }

    [Theory]
    [InlineData("movies like superbad", "superbad")]
    [InlineData("like superbad", "superbad")]
    [InlineData("similar to the conjuring", "the conjuring")]
    [InlineData("shows like breaking bad", "breaking bad")]
    [InlineData("films similar to  Up ", "Up")]
    [InlineData("something like john wick", "john wick")]
    [InlineData("godfather type movies", "godfather")]
    [InlineData("heat style films", "heat")]
    [InlineData("movies in the vein of heat", "heat")]
    public void Detects_similar_to(string query, string expectedTitle)
    {
        var c = QueryClassifier.Classify(query, semanticAvailable: true);
        Assert.Equal(QueryMode.SimilarTo, c.Mode);
        Assert.Equal(expectedTitle, c.SimilarToTitle);
        Assert.Equal(1.0, c.SemanticRatio);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_is_passthrough(string? query)
        => Assert.Equal(QueryMode.Passthrough, QueryClassifier.Classify(query, true).Mode);

    [Fact]
    public void No_semantic_available_is_bm25_only()
        => Assert.Equal(QueryMode.Bm25Only,
            QueryClassifier.Classify("feel good movies", semanticAvailable: false).Mode);

    [Fact]
    public void Ratios_match_plan()
    {
        Assert.Equal(0.7, QueryClassifier.Classify("feel good movies", true).SemanticRatio);
        // Title-shaped queries carry no ratio: pure stock BM25, no embed call.
        Assert.Null(QueryClassifier.Classify("breaking bad", true).SemanticRatio);
    }
}
