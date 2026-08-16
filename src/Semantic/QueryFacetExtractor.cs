using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Pulls hard facets out of natural queries and converts them to Meilisearch
/// filters: "90s heist movies" → filter decade=1990s + query "heist movies";
/// "under 90 minutes", "rated pg-13" likewise. No LLM, pure patterns.
/// </summary>
public static partial class QueryFacetExtractor
{
    [GeneratedRegex(@"\b(?:from\s+the\s+)?(?:(19|20)(\d)0'?s|(\d)0'?s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecadePattern();

    [GeneratedRegex(@"\b(under|less\s+than|shorter\s+than|over|longer\s+than|more\s+than)\s+(\d+(?:\.\d+)?)\s*(minutes?|mins?|hours?|hrs?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimePattern();

    [GeneratedRegex(@"\b(?:rated\s+(pg-13|nc-17|tv-ma|tv-14|tv-pg|pg|g|r)|(pg-13|nc-17|tv-ma|tv-14|tv-pg|pg)[\s-]rated|(pg-13|nc-17|tv-ma|tv-14|pg)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RatingPattern();

    private const string SeriesType = "MediaBrowser.Controller.Entities.TV.Series";
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    // Media-type intent, conservatively: leading "tv show(s)/series about|with|…"
    // or a trailing "tv show(s)/series". Bare "show" stays untouched so titles
    // like "the truman show" survive.
    [GeneratedRegex(@"^(?:a\s+)?(?:tv\s+)?(?:shows?|series)\s+(?=about|with|where|that|like|similar)|(?:\s+tv\s+shows?|\s+series)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesIntentPattern();

    [GeneratedRegex(@"^(?:a\s+)?(?:movies?|films?)\s+(?=about|with|where|that)", RegexOptions.IgnoreCase)]
    private static partial Regex MovieIntentPattern();

    public static (string CleanedQuery, IReadOnlyList<string> Filters) Extract(string query)
    {
        var filters = new List<string>();
        var cleaned = query;

        if (SeriesIntentPattern().IsMatch(cleaned))
        {
            filters.Add($"type = \"{SeriesType}\"");
            cleaned = SeriesIntentPattern().Replace(cleaned, " ");
        }
        else if (MovieIntentPattern().IsMatch(cleaned))
        {
            filters.Add($"type = \"{MovieType}\"");
            cleaned = MovieIntentPattern().Replace(cleaned, " ");
        }

        cleaned = DecadePattern().Replace(cleaned, m =>
        {
            var century = m.Groups[1].Success ? m.Groups[1].Value : null;
            var tens = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            // bare "20s"–"90s" → 19xx; "00s"/"10s" → 20xx
            century ??= int.Parse(tens, CultureInfo.InvariantCulture) >= 2 ? "19" : "20";
            filters.Add($"decade = \"{century}{tens}0s\"");
            return " ";
        });

        cleaned = RuntimePattern().Replace(cleaned, m =>
        {
            var op = m.Groups[1].Value.StartsWith("under", StringComparison.OrdinalIgnoreCase)
                     || m.Groups[1].Value.StartsWith("less", StringComparison.OrdinalIgnoreCase)
                     || m.Groups[1].Value.StartsWith("shorter", StringComparison.OrdinalIgnoreCase)
                ? "<" : ">";
            // the query is English regardless of server locale: "1.5 hours" is
            // 15 under a comma-decimal culture
            var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var minutes = m.Groups[3].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase)
                ? (int)(value * 60) : (int)value;
            filters.Add($"runtimeMinutes {op} {minutes}");
            return " ";
        });

        cleaned = RatingPattern().Replace(cleaned, m =>
        {
            var rating = (m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToUpperInvariant();
            filters.Add($"officialRating = \"{rating}\"");
            return " ";
        });

        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return (cleaned.Length == 0 ? query : cleaned, filters);
    }
}
