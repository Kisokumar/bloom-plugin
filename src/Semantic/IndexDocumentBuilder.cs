using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Builds the embed text and the staleness hash for an item.
/// Pure functions; MeilisearchItem is a plain record with no Jellyfin coupling.
/// </summary>
public static class IndexDocumentBuilder
{
    /// <summary>
    /// Template v3: {genres}. {tags}. {people}. {overview} {tagline} {name}, {type} {year}.
    /// Tags/genres lead and the title trails: leading title tokens dominate the
    /// embedding and pollute vibe queries (validated against the real library,
    /// 2026-08-03). People carry actor/director adjacency ("movies like heat").
    /// Changing this invalidates embedHash and re-embeds naturally.
    /// </summary>
    public static string BuildEmbedText(MeilisearchItem item)
    {
        var sb = new StringBuilder();
        AppendList(sb, item.Genres);
        AppendList(sb, item.Tags);
        AppendList(sb, item.People);
        if (!string.IsNullOrWhiteSpace(item.Overview))
            sb.Append(item.Overview).Append(' ');
        if (!string.IsNullOrWhiteSpace(item.Tagline))
            sb.Append(item.Tagline).Append(' ');
        sb.Append(item.Name).Append(", ").Append(FriendlyType(item.Type));
        if (item.ProductionYear is { } year)
            sb.Append(' ').Append(year);
        sb.Append('.');
        if (item.Decade != null)
            sb.Append(' ').Append(item.Decade).Append('.');
        if (!string.IsNullOrEmpty(item.OfficialRating))
            sb.Append(" Rated ").Append(item.OfficialRating).Append('.');
        return sb.ToString().Trim();
    }

    public static string ComputeEmbedHash(string embedText, string modelId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(embedText + "\n" + modelId));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>"MediaBrowser.Controller.Entities.Movies.Movie" → "Movie".</summary>
    private static string FriendlyType(string? fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName))
            return "Item";
        var idx = fullTypeName.LastIndexOf('.');
        return idx >= 0 ? fullTypeName[(idx + 1)..] : fullTypeName;
    }

    private static void AppendList(StringBuilder sb, string[]? values)
    {
        if (values is { Length: > 0 })
            sb.Append(string.Join(", ", values)).Append(". ");
    }
}
