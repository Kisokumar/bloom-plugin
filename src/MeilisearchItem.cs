namespace Jellyfin.Plugin.Meilisearch;

public record MeilisearchItem(
    string Guid,
    string? Type,
    string? ParentId,
    string? Name,
    string? Overview,
    string? OriginalTitle,
    string? SeriesName,
    int? ProductionYear,
    string[]? Artists,
    string[]? AlbumArtists,
    string[]? Genres,
    string[]? Studios,
    string[]? Tags,
    bool? IsFolder,
    double? CommunityRating,
    double? CriticRating,
    string? Path,
    string? Tagline,
    string? SortName,
    string[]? People = null,
    string? OfficialRating = null,
    int? RuntimeMinutes = null,
    string? Decade = null,
    // indexed as a filter only — nothing queries it yet, but backfilling it
    // later would mean a full re-index of every library
    string[]? ProductionLocations = null
);
