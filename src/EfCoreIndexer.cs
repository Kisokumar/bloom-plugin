using System.Collections.Immutable;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class EfCoreIndexer(
    IJellyfinDatabaseProvider dbProvider,
    MeilisearchClientHolder clientHolder,
    ILogger<EfCoreIndexer> logger
) : Indexer(clientHolder, logger)
{
    protected override async Task<ImmutableList<MeilisearchItem>> GetItems(IReadOnlySet<string> includedTypes)
    {
        using var context = dbProvider.DbContextFactory!.CreateDbContext();
        Status["Database"] = context.Database.GetDbConnection().ConnectionString;

        var entities = await context.BaseItems
            .AsNoTracking()
            .Where(x => x.Type != null && includedTypes.Contains(x.Type))
            .ToListAsync();

        // Top-billed cast + directors (semantic + keyword signal). One filtered
        // pass over the map table, grouped in memory.
        var peopleRows = await context.PeopleBaseItemMap
            .AsNoTracking()
            .Where(m => (m.SortOrder != null && m.SortOrder < 6) || m.People.PersonType == "Director")
            .Select(m => new { m.ItemId, m.People.Name, m.SortOrder })
            .ToListAsync();
        var peopleByItem = peopleRows
            .GroupBy(r => r.ItemId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.SortOrder ?? int.MaxValue)
                    .Select(r => r.Name).Distinct().Take(8).ToArray());

        return entities
            .Select(e => ToMeilisearchItem(e, peopleByItem.GetValueOrDefault(e.Id)))
            .ToImmutableList();
    }

    private static MeilisearchItem ToMeilisearchItem(BaseItemEntity item, string[]? people)
    {
        return new MeilisearchItem(
            Guid: item.Id.ToString(),
            Type: item.Type,
            ParentId: item.ParentId.ToString(),
            Name: item.Name,
            Overview: item.Overview,
            OriginalTitle: item.OriginalTitle,
            SeriesName: item.SeriesName,
            Studios: item.Studios?.Split('|'),
            Genres: item.Genres?.Split('|'),
            Tags: item.Tags?.Split('|'),
            CommunityRating: item.CommunityRating,
            ProductionYear: item.ProductionYear,
            Path: item.Path?[0] == '%' ? null : item.Path,
            Artists: item.Artists?.Split('|'),
            AlbumArtists: item.AlbumArtists?.Split('|'),
            CriticRating: item.CriticRating,
            IsFolder: item.IsFolder,
            Tagline: item.Tagline,
            SortName: item.SortName,
            People: people,
            OfficialRating: item.OfficialRating,
            RuntimeMinutes: item.RunTimeTicks is { } ticks and > 0 ? (int)(ticks / 600_000_000) : null,
            Decade: item.ProductionYear is { } y ? $"{y / 10 * 10}s" : null,
            ProductionLocations: item.ProductionLocations?.Split('|')
        );
    }
}
