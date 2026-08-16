using Jellyfin.Plugin.Meilisearch.Semantic;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class FranchiseMatcherTests
{
    [Theory]
    [InlineData("The Godfather", "The Godfather Part III")]
    [InlineData("The Godfather", "The Godfather: Part II")]
    [InlineData("21 Jump Street", "22 Jump Street")]
    [InlineData("Scary Movie", "Scary Movie 5")]
    [InlineData("Alien", "Aliens")]
    [InlineData("Evil Dead II", "Evil Dead Rise")]
    public void Sequels_match(string a, string b)
        => Assert.True(FranchiseMatcher.IsSameFranchise(a, b));

    [Theory]
    [InlineData("The Godfather", "GoodFellas")]
    [InlineData("The Godfather", "A Bronx Tale")]
    [InlineData("The Godfather", "The Family")]
    [InlineData("Superbad", "Booksmart")]
    [InlineData("Heat", "Casino")]
    [InlineData("Scary Movie", "Scary Stories to Tell in the Dark")]
    public void Unrelated_do_not_match(string a, string b)
        => Assert.False(FranchiseMatcher.IsSameFranchise(a, b));
}
