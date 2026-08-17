using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Meilisearch.Adapters;

/// <summary>In-memory ring of recent searches for the admin page (top/zero-result reporting).</summary>
public class SearchAnalytics
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<Entry> _entries = new();

    public record Entry(DateTime At, string Term, string Mode, int Results, long Ms);

    public void Record(string term, string mode, int results, long ms)
    {
        _entries.Enqueue(new Entry(DateTime.UtcNow, term, mode, results, ms));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public object Summarize()
    {
        var all = _entries.ToArray();
        return new
        {
            total = all.Length,
            byMode = all.GroupBy(e => e.Mode).ToDictionary(g => g.Key, g => g.Count()),
            topQueries = all.GroupBy(e => e.Term.ToLowerInvariant())
                .OrderByDescending(g => g.Count()).Take(15)
                .Select(g => new { term = g.Key, count = g.Count() }),
            zeroResults = all.Where(e => e.Results == 0)
                .GroupBy(e => e.Term.ToLowerInvariant())
                .OrderByDescending(g => g.Count()).Take(15)
                .Select(g => new { term = g.Key, count = g.Count() }),
            avgMs = all.Length == 0 ? 0 : (long)all.Average(e => e.Ms),
            recent = all.TakeLast(20).Reverse()
                .Select(e => new { e.At, e.Term, e.Mode, e.Results, e.Ms }),
        };
    }
}
