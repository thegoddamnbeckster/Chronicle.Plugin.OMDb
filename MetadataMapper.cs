using System.Text.Json;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OMDb;

/// <summary>
/// Turns an OMDb record into Chronicle's metadata shape. "N/A" is nothing, never text. Everything OMDb supplied that has no
/// first-class field is kept in ExtendedData: the Rotten Tomatoes and Metacritic scores (Chronicle has one rating field, and it holds
/// the IMDb one), awards, box office, the age rating, languages, countries.
/// </summary>
internal static class MetadataMapper
{
    public const string Source = "omdb";

    public static MediaMetadata Record(OmdbRecord r)
    {
        var imdb = TextRules.Value(r.ImdbId) ?? "";
        var ids = new Dictionary<string, string>();
        if (imdb.Length > 0) ids["imdb"] = imdb;

        var ratings = (r.Ratings ?? [])
            .Where(x => TextRules.Value(x.Source) is not null && TextRules.Value(x.Value) is not null)
            .Select(x => new Dictionary<string, object?> { ["source"] = x.Source!.Trim(), ["value"] = x.Value!.Trim() }).ToList();
        double? Rated(string source) => (r.Ratings ?? []).FirstOrDefault(x => string.Equals(x.Source?.Trim(), source, StringComparison.OrdinalIgnoreCase)) is { } hit
            ? TextRules.Score(hit.Value) : null;

        return new MediaMetadata
        {
            ExternalId = imdb,
            Source = Source,
            Title = TextRules.Value(r.Title) ?? "",
            Overview = TextRules.Value(r.Plot),
            Year = TextRules.YearOf(r.Year) ?? TextRules.YearOf(r.Released),
            PosterUrl = TextRules.Value(r.Poster),
            RuntimeMinutes = TextRules.RuntimeMinutes(r.Runtime),
            Genres = TextRules.List(r.Genre),
            Rating = TextRules.Decimal(r.ImdbRating),
            Cast = TextRules.List(r.Actors).Select(a => new CastMember(a)).ToList(),
            Crew = CrewOf(r),
            ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["ids"] = ids,
                ["external_ids"] = ids,
                ["imdb_id"] = TextRules.Value(r.ImdbId),
                ["type"] = TextRules.Value(r.Type),
                ["rated"] = TextRules.Value(r.Rated),
                ["released"] = TextRules.Value(r.Released),
                ["released_iso"] = TextRules.IsoDate(r.Released),
                ["languages"] = TextRules.List(r.Language),
                ["countries"] = TextRules.List(r.Country),
                ["awards"] = TextRules.Value(r.Awards),
                ["box_office"] = TextRules.Value(r.BoxOffice),
                ["dvd"] = TextRules.Value(r.Dvd),
                ["production"] = TextRules.Value(r.Production),
                ["website"] = TextRules.Value(r.Website),
                ["ratings"] = ratings,
                ["imdb_rating"] = TextRules.Decimal(r.ImdbRating),
                ["imdb_votes"] = TextRules.Count(r.ImdbVotes),
                ["rotten_tomatoes_percent"] = Rated("Rotten Tomatoes"),
                ["metacritic_score"] = TextRules.Count(r.Metascore) ?? (int?)Rated("Metacritic"),
                ["total_seasons"] = TextRules.Count(r.TotalSeasons),
                ["season"] = TextRules.Count(r.Season),
                ["episode"] = TextRules.Count(r.Episode),
                ["series_imdb_id"] = TextRules.Value(r.SeriesId),
            }),
        };
    }

    /// <summary>A search hit: what the search itself returned (no plot, no runtime).</summary>
    public static MediaMetadata FromSearch(SearchHit hit) => new()
    {
        ExternalId = TextRules.Value(hit.ImdbId) ?? "",
        Source = Source,
        Title = TextRules.Value(hit.Title) ?? "",
        Year = TextRules.YearOf(hit.Year),
        PosterUrl = TextRules.Value(hit.Poster),
        ExtendedData = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["imdb_id"] = TextRules.Value(hit.ImdbId),
            ["type"] = TextRules.Value(hit.Type),
        }),
    };

    private static List<CrewMember> CrewOf(OmdbRecord r)
    {
        var crew = new List<CrewMember>();
        foreach (var director in TextRules.List(r.Director))
            crew.Add(new CrewMember(TextRules.NameAndCredit(director).Name, "Director"));
        foreach (var writer in TextRules.List(r.Writer))
        {
            var (name, credit) = TextRules.NameAndCredit(writer);
            crew.Add(new CrewMember(name, credit is null ? "Writer" : $"Writer ({credit})"));
        }
        return crew;
    }
}
