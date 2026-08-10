namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Heuristic "same franchise" check for SimilarTo results: sequels carry the
/// parent title in their name ("The Godfather Part III", "22 Jump Street",
/// "Scary Movie 5"). Used to demote (never exclude) franchise-mates, since
/// the heuristic can't catch renamed sequels (Rambo/First Blood) and users
/// sometimes do want them.
/// </summary>
public static class FranchiseMatcher
{
    private static readonly HashSet<string> NoiseTokens =
    [
        "the", "a", "an", "part", "chapter", "volume", "vol", "episode", "saga",
        "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii",
    ];

    public static bool IsSameFranchise(string? a, string? b)
    {
        var ta = SignificantTokens(a);
        var tb = SignificantTokens(b);
        if (ta.Count == 0 || tb.Count == 0)
            return false;

        var (small, large) = ta.Count <= tb.Count ? (ta, tb) : (tb, ta);
        var overlap = small.Count(large.Contains);
        if (overlap == small.Count)
            return true;

        return (double)overlap / (small.Count + large.Count - overlap) >= 0.6;
    }

    private static HashSet<string> SignificantTokens(string? name)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
            return tokens;

        foreach (var raw in name.ToLowerInvariant().Split(
                     [' ', ':', '-', '–', ',', '.', '!', '?', '\'', '(', ')', '&'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.All(char.IsDigit) ? "" : raw.TrimEnd('s'); // crude plural fold: alien/aliens
            if (token.Length > 0 && !NoiseTokens.Contains(token))
                tokens.Add(token);
        }

        return tokens;
    }
}
