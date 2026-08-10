using Jellyfin.Plugin.Meilisearch.Semantic;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class SimilarityRescorerTests
{
    [Fact]
    public void Highly_rated_classic_beats_closer_but_trashy_neighbor()
    {
        // 365 Days: closer vector (0.882) but community 3.3.
        var junk = SimilarityRescorer.Blend(0.882, 3.3, null);
        // GoodFellas: slightly farther (0.875) but community 8.7.
        var classic = SimilarityRescorer.Blend(0.875, 8.7, null);
        Assert.True(classic > junk);
    }

    [Fact]
    public void Critic_rating_used_when_community_missing()
    {
        var withCritic = SimilarityRescorer.Blend(0.9, null, 80);
        var neutral = SimilarityRescorer.Blend(0.9, null, null);
        Assert.True(withCritic > neutral);
    }

    [Fact]
    public void Rating_never_overrides_big_similarity_gap()
    {
        var relevant = SimilarityRescorer.Blend(0.90, 6.0, null);
        var popularButUnrelated = SimilarityRescorer.Blend(0.55, 9.5, null);
        Assert.True(relevant > popularButUnrelated);
    }
}
