using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Heuristic query router. No I/O, no Jellyfin types; pure function of the query string.
/// </summary>
public static partial class QueryClassifier
{
    [GeneratedRegex(@"^\s*(?:movies?|shows?|series|films?|something|anything)?\s*(?:like|similar\s+to)\s+(?<title>.{2,})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SimilarToPattern();

    // "godfather type movies", "heat style films", "movies in the vein of heat".
    // Safe to over-match: an unresolvable title falls back to SemanticDominant.
    [GeneratedRegex(@"^\s*(?<title>.{2,}?)[\s-]+(?:type|style|esque)\s+(?:movies?|films?|shows?|series)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TypeStylePattern();

    [GeneratedRegex(@"^\s*(?:movies?|films?|shows?|series)\s+in\s+the\s+(?:vein|style|spirit)\s+of\s+(?<title>.{2,})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VeinOfPattern();

    private static readonly HashSet<string> MediaNouns =
    [
        "movie", "movies", "film", "films", "show", "shows", "series", "tv",
        "comedies", "thrillers", "dramas", "documentaries", "musicals",
        "westerns", "cartoons", "anime", "specials", "albums", "songs",
    ];

    // Phrasing that signals "describe by meaning", not "match my words".
    private static readonly string[] VibeMarkers =
    [
        "about", "feel good", "feel-good", "vibe", "vibes", "mood",
        "but funny", "but scary", "something", "movies for", "films for",
    ];

    public static QueryClassification Classify(string? searchTerm, bool semanticAvailable)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return QueryClassification.Passthrough;

        if (!semanticAvailable)
            return QueryClassification.Bm25Only;

        var term = searchTerm.Trim();

        foreach (var pattern in (Regex[])[SimilarToPattern(), TypeStylePattern(), VeinOfPattern()])
        {
            var similar = pattern.Match(term);
            if (similar.Success)
                return QueryClassification.SimilarTo(similar.Groups["title"].Value.Trim().Trim('"', '\''));
        }

        // Explicitly quoted → the user wants literal matching.
        if (term.Length >= 2 && (term[0] == '"' || term[0] == '\'') && term[^1] == term[0])
            return QueryClassification.Bm25Dominant();

        var lower = term.ToLowerInvariant();
        foreach (var marker in VibeMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return QueryClassification.SemanticDominant();
        }

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A trailing media noun ("scary movies", "heist films") is browse
        // intent, not a title; route to semantic even at 1–2 tokens.
        if (tokens.Length > 0 && MediaNouns.Contains(tokens[^1].ToLowerInvariant()))
            return QueryClassification.SemanticDominant();

        return tokens.Length <= 2
            ? QueryClassification.Bm25Dominant()
            : QueryClassification.SemanticDominant();
    }
}
