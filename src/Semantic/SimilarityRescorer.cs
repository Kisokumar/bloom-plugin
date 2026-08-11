namespace Jellyfin.Plugin.Meilisearch.Semantic;

/// <summary>
/// Blends vector similarity with audience rating for SimilarTo ranking.
/// Neighbor cosine scores cluster tightly (~0.85–0.93), so a 25% rating term
/// is enough to sink low-rated topical matches below well-loved ones without
/// letting popularity override actual similarity.
/// </summary>
public static class SimilarityRescorer
{
    private const double VectorWeight = 0.75;
    private const double NeutralRating10 = 6.0;

    /// <param name="vectorScore">Meilisearch similarity ranking score, 0–1.</param>
    /// <param name="communityRating">0–10 scale, may be null.</param>
    /// <param name="criticRating">0–100 scale, may be null.</param>
    public static double Blend(double vectorScore, double? communityRating, double? criticRating)
    {
        var rating10 = communityRating ?? criticRating / 10.0 ?? NeutralRating10;
        rating10 = Math.Clamp(rating10, 0, 10);
        return VectorWeight * vectorScore + (1 - VectorWeight) * (rating10 / 10.0);
    }
}
