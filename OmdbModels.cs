using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronicle.Plugin.OMDb;

// OMDb answers every field as text, "N/A" where it has nothing. Only what is used is declared.

/// <summary>
/// Reads any JSON value as the text it is. OMDb documents text, but a number or a list where text belongs must not make the whole
/// record unreadable (and the item Failed): the value is kept as its JSON text and null is nothing.
/// </summary>
internal sealed class TextOrAnythingConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Null => null,
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

internal sealed class RatingEntry
{
    [JsonPropertyName("Source")] public string? Source { get; set; }
    [JsonPropertyName("Value")] public string? Value { get; set; }
}

/// <summary>One title: a movie, a series or an episode.</summary>
internal sealed class OmdbRecord
{
    [JsonPropertyName("Response")] public string? Response { get; set; }
    [JsonPropertyName("Error")] public string? Error { get; set; }

    [JsonPropertyName("Title")] public string? Title { get; set; }
    [JsonPropertyName("Year")] public string? Year { get; set; }
    [JsonPropertyName("Rated")] public string? Rated { get; set; }
    [JsonPropertyName("Released")] public string? Released { get; set; }
    [JsonPropertyName("Runtime")] public string? Runtime { get; set; }
    [JsonPropertyName("Genre")] public string? Genre { get; set; }
    [JsonPropertyName("Director")] public string? Director { get; set; }
    [JsonPropertyName("Writer")] public string? Writer { get; set; }
    [JsonPropertyName("Actors")] public string? Actors { get; set; }
    [JsonPropertyName("Plot")] public string? Plot { get; set; }
    [JsonPropertyName("Language")] public string? Language { get; set; }
    [JsonPropertyName("Country")] public string? Country { get; set; }
    [JsonPropertyName("Awards")] public string? Awards { get; set; }
    [JsonPropertyName("Poster")] public string? Poster { get; set; }
    [JsonPropertyName("Ratings")] public List<RatingEntry>? Ratings { get; set; }
    [JsonPropertyName("Metascore")] public string? Metascore { get; set; }
    [JsonPropertyName("imdbRating")] public string? ImdbRating { get; set; }
    [JsonPropertyName("imdbVotes")] public string? ImdbVotes { get; set; }
    [JsonPropertyName("imdbID")] public string? ImdbId { get; set; }
    [JsonPropertyName("Type")] public string? Type { get; set; }
    [JsonPropertyName("DVD")] public string? Dvd { get; set; }
    [JsonPropertyName("BoxOffice")] public string? BoxOffice { get; set; }
    [JsonPropertyName("Production")] public string? Production { get; set; }
    [JsonPropertyName("Website")] public string? Website { get; set; }
    [JsonPropertyName("totalSeasons")] public string? TotalSeasons { get; set; }
    [JsonPropertyName("Season")] public string? Season { get; set; }
    [JsonPropertyName("Episode")] public string? Episode { get; set; }
    [JsonPropertyName("seriesID")] public string? SeriesId { get; set; }

    public bool Ok => string.Equals(Response, "True", StringComparison.OrdinalIgnoreCase);
}

internal sealed class SearchHit
{
    [JsonPropertyName("Title")] public string? Title { get; set; }
    [JsonPropertyName("Year")] public string? Year { get; set; }
    [JsonPropertyName("imdbID")] public string? ImdbId { get; set; }
    [JsonPropertyName("Type")] public string? Type { get; set; }
    [JsonPropertyName("Poster")] public string? Poster { get; set; }
}

internal sealed class SearchResponse
{
    [JsonPropertyName("Response")] public string? Response { get; set; }
    [JsonPropertyName("Error")] public string? Error { get; set; }
    [JsonPropertyName("Search")] public List<SearchHit>? Search { get; set; }
    [JsonPropertyName("totalResults")] public string? TotalResults { get; set; }
}
