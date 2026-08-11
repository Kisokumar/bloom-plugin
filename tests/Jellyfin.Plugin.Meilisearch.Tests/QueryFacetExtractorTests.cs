using Jellyfin.Plugin.Meilisearch.Semantic;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class QueryFacetExtractorTests
{
    [Theory]
    [InlineData("80s heist movies", "heist movies", "decade = \"1980s\"")]
    [InlineData("movies from the 90s", "movies", "decade = \"1990s\"")]
    [InlineData("1970s thrillers", "thrillers", "decade = \"1970s\"")]
    [InlineData("00s pop", "pop", "decade = \"2000s\"")]
    [InlineData("comedies under 90 minutes", "comedies", "runtimeMinutes < 90")]
    [InlineData("epics over 3 hours", "epics", "runtimeMinutes > 180")]
    [InlineData("rated pg adventure movies", "adventure movies", "officialRating = \"PG\"")]
    [InlineData("pg-13 action", "action", "officialRating = \"PG-13\"")]
    public void Extracts_single_facet(string query, string cleaned, string filter)
    {
        var (c, f) = QueryFacetExtractor.Extract(query);
        Assert.Equal(cleaned, c);
        Assert.Equal([filter], f);
    }

    [Fact]
    public void Extracts_multiple_facets()
    {
        var (c, f) = QueryFacetExtractor.Extract("80s horror under 100 minutes");
        Assert.Equal("horror", c);
        Assert.Contains("decade = \"1980s\"", f);
        Assert.Contains("runtimeMinutes < 100", f);
    }

    [Theory]
    [InlineData("tv show about survivors of a zombie apocalypse", "about survivors of a zombie apocalypse", "Series")]
    [InlineData("series about a chemistry teacher", "about a chemistry teacher", "Series")]
    [InlineData("zombie tv shows", "zombie", "Series")]
    [InlineData("movies about time travel", "about time travel", "Movie")]
    public void Extracts_media_type(string query, string cleaned, string type)
    {
        var (c, f) = QueryFacetExtractor.Extract(query);
        Assert.Equal(cleaned, c);
        Assert.Single(f);
        Assert.Contains(type, f[0]);
    }

    [Theory]
    [InlineData("feel good movies")]
    [InlineData("movies like the godfather")]
    [InlineData("breaking bad")]
    [InlineData("the truman show")]
    public void Leaves_normal_queries_alone(string query)
    {
        var (c, f) = QueryFacetExtractor.Extract(query);
        Assert.Equal(query, c);
        Assert.Empty(f);
    }

    [Fact]
    public void Never_returns_empty_query()
    {
        var (c, _) = QueryFacetExtractor.Extract("80s");
        Assert.False(string.IsNullOrWhiteSpace(c));
    }
}
