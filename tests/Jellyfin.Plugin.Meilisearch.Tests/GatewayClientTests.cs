using Jellyfin.Plugin.Meilisearch.Semantic;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class GatewayClientTests
{
    [Theory]
    [InlineData("type = \"MediaBrowser.Controller.Entities.Movies.Movie\"", "Movie", "")]
    [InlineData("type = \"MediaBrowser.Controller.Entities.Movies.Movie\" OR type = \"MediaBrowser.Controller.Entities.TV.Series\"",
        "Movie,Series", "")]
    [InlineData("type = \"MediaBrowser.Controller.Entities.Movies.Movie\" OR type = \"MediaBrowser.Controller.Entities.Audio.MusicAlbum\"",
        "Movie", "MediaBrowser.Controller.Entities.Audio.MusicAlbum")]
    [InlineData("type = \"MediaBrowser.Controller.Entities.Audio.MusicAlbum\"",
        "", "MediaBrowser.Controller.Entities.Audio.MusicAlbum")]
    [InlineData("", "", "")]
    [InlineData("communityRating > 5", "", "")]
    public void SplitTypes_SeparatesSupportedFromRest(string filter, string supported, string unsupported)
    {
        var (sup, unsup) = GatewayClient.SplitTypes(filter);
        Assert.Equal(supported, string.Join(",", sup));
        Assert.Equal(unsupported, string.Join(",", unsup));
    }

    [Fact]
    public void ParseIds_ReadsDashedAndUndashedGuids()
    {
        var json = """
            {"intent":{"mode":"semantic"},"results":[
              {"id":"11111111-2222-3333-4444-555555555555","name":"A"},
              {"id":"66666666777788889999aaaaaaaaaaaa","name":"B"},
              {"id":null,"name":"C"},
              {"id":"not-a-guid","name":"D"}]}
            """;
        var ids = GatewayClient.ParseIds(json);
        Assert.NotNull(ids);
        Assert.Equal(2, ids.Count);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), ids[0]);
    }

    [Fact]
    public void ParseIds_ReturnsNullOnGarbage()
    {
        Assert.Null(GatewayClient.ParseIds("not json"));
        Assert.Null(GatewayClient.ParseIds("{\"unexpected\":true}"));
        Assert.Empty(GatewayClient.ParseIds("{\"results\":[]}")!);
    }
}
