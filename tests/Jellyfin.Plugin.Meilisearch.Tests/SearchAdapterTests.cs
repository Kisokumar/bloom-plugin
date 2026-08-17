using Xunit;
using Jellyfin.Plugin.Meilisearch.Adapters;

namespace Jellyfin.Plugin.Meilisearch.Tests;

/// <summary>
/// Every search goes through this paging arithmetic, and the server reports no
/// total of its own, so getting it wrong either truncates results or makes a
/// client page forever. None of it was covered.
/// </summary>
public class SearchAdapterTests
{
    private static IReadOnlyList<Guid> Ids(int n) =>
        Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void First_page_is_the_first_slice()
    {
        var ids = Ids(50);
        var (page, _) = JellyfinSearchAdapter.Paginate(ids, offset: 0, limit: 20);
        Assert.Equal(20, page.Count);
        Assert.Equal(ids.Take(20), page);
    }

    [Fact]
    public void Later_pages_skip_what_came_before()
    {
        var ids = Ids(50);
        var (page, _) = JellyfinSearchAdapter.Paginate(ids, offset: 20, limit: 20);
        Assert.Equal(ids.Skip(20).Take(20), page);
    }

    [Fact]
    public void A_short_result_means_we_have_seen_everything()
    {
        // 12 ids for a 20-wide window: there is no more, so the total is exact
        // and a client stops paging.
        var (page, total) = JellyfinSearchAdapter.Paginate(Ids(12), offset: 0, limit: 20);
        Assert.Equal(12, page.Count);
        Assert.Equal(12, total);
    }

    [Fact]
    public void A_full_page_promises_at_least_one_more()
    {
        // Exactly filling the window is indistinguishable from "there is more",
        // so the total must over-report -- under-reporting stops a client paging
        // while results remain.
        var (page, total) = JellyfinSearchAdapter.Paginate(Ids(40), offset: 0, limit: 20);
        Assert.Equal(20, page.Count);
        Assert.True(total > 20, "a full page must leave room for another");
    }

    [Fact]
    public void Paging_past_the_end_yields_nothing_rather_than_throwing()
    {
        var (page, _) = JellyfinSearchAdapter.Paginate(Ids(10), offset: 100, limit: 20);
        Assert.Empty(page);
    }

    [Fact]
    public void Empty_first_page_hands_over_to_keyword()
    {
        // The server had no answer, so keyword deserves a turn.
        Assert.True(JellyfinSearchAdapter.ShouldFallThrough(pageCount: 0, offset: 0));
    }

    [Fact]
    public void Empty_later_page_is_the_end_not_a_failure()
    {
        // Falling through here would resurface titles the server already
        // rejected, as a second page of unrelated results.
        Assert.False(JellyfinSearchAdapter.ShouldFallThrough(pageCount: 0, offset: 20));
    }

    [Fact]
    public void A_page_with_results_never_falls_through()
    {
        Assert.False(JellyfinSearchAdapter.ShouldFallThrough(pageCount: 5, offset: 0));
        Assert.False(JellyfinSearchAdapter.ShouldFallThrough(pageCount: 5, offset: 20));
    }
}
