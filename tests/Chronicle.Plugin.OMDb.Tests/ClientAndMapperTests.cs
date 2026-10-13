using System.Net;
using System.Text.Json;
using Chronicle.Plugin.OMDb;
using Chronicle.Plugins;

namespace Chronicle.Plugin.OMDb.Tests;

public class ClientTests
{
    private static async Task<OmdbRecord?> Get(FakeOmdb fake, string id) => await fake.Client().GetByIdAsync(id, true, CancellationToken.None);

    [Fact]
    public async Task ATitle_IsFetchedById_WithTheFullPlot_InOneRequest()
    {
        var fake = FakeOmdb.Catalogue();
        var record = await Get(fake, "tt0133093");

        Assert.Equal("The Matrix", record!.Title);
        Assert.Equal(1, fake.Requests);
        Assert.Equal("r=json&i=tt0133093&plot=full", fake.Queries[0]);
    }

    [Fact]
    public async Task TheShortPlot_CanBeAsked()
    {
        var fake = FakeOmdb.Catalogue();
        await fake.Client().GetByIdAsync("tt0133093", fullPlot: false, CancellationToken.None);
        Assert.Contains("plot=short", fake.Queries[0]);
    }

    [Theory]
    [InlineData("tt9999999999")]   // "Incorrect IMDb ID."
    [InlineData("tt14891408")]     // "Error getting data.": OMDb's database lacks a title IMDb has
    public async Task ATitleOmdbDoesNotHave_IsANormalNotFound(string id) => Assert.Null(await Get(FakeOmdb.Catalogue(), id));

    [Fact]
    public async Task ATitleSearchThatFindsNothing_IsNotFound()
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Null(await fake.Client().GetByTitleAsync("zzzzqqqxxnotamovie", null, "movie", true, CancellationToken.None));
    }

    [Fact]
    public async Task TheDaysQuotaUsedUp_IsThrownAs429_NotWaitedOut_AndNotRetried()
    {
        var fake = new FakeOmdb { FailWith = HttpStatusCode.Unauthorized, FailBody = FakeOmdb.Fixture("error_limit_reached.json") };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(fake, "tt0133093"));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, fake.Requests);
        Assert.Empty(fake.Delays.Where(d => d > TimeSpan.FromSeconds(1)));   // waiting for tomorrow is not an option
    }

    [Fact]
    public async Task TheQuotaMessage_WithHttp200_IsAlsoA429()
    {
        var fake = new FakeOmdb().RouteBody(_ => true, FakeOmdb.Fixture("error_limit_reached.json"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(fake, "tt0133093"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task AnInvalidKey_IsAnAuthFailure_WithAMessageThatNamesTheFix()
    {
        var fake = new FakeOmdb { FailWith = HttpStatusCode.Unauthorized, FailBody = FakeOmdb.Fixture("error_invalid_key.json") };

        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => Get(fake, "tt0133093"));

        Assert.Equal("chronicle.plugin.omdb", ex.PluginId);
        Assert.Contains("settings", ex.Message);
        Assert.Contains("activated", ex.Message);   // an unactivated key is the usual cause
        Assert.DoesNotContain(FakeOmdb.TestKey, ex.Message);
    }

    [Fact]
    public async Task ServerErrors_Propagate_SoChronicleCanRetry()
    {
        var fake = new FakeOmdb { FailWith = HttpStatusCode.BadGateway };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Get(fake, "tt0133093"));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJson_IsAnError_NotAnEmptyRecord()
    {
        var fake = new FakeOmdb().RouteBody(_ => true, "<html>maintenance</html>");
        await Assert.ThrowsAsync<HttpRequestException>(() => Get(fake, "tt0133093"));
    }

    [Fact]
    public async Task AQueryOmdbCannotRead_IsNotFound_NotAFailure()
    {
        var fake = new FakeOmdb().RouteBody(_ => true, "{\"Response\":\"False\",\"Error\":\"Conversion from string \\\"x\\\" to type 'Double' is not valid.\"}");
        Assert.Null(await Get(fake, "tt0133093"));
    }

    [Fact]
    public async Task TheKey_IsSentOnce_AsApikey_AndNeverAppearsAnywhereElse()
    {
        var fake = FakeOmdb.Catalogue();
        await Get(fake, "tt0133093");

        Assert.Single(fake.RawUrls);
        Assert.Contains($"apikey={FakeOmdb.TestKey}", fake.RawUrls[0]);
        Assert.DoesNotContain(FakeOmdb.TestKey, fake.Queries[0]);
    }

    [Fact]
    public async Task AFailure_NeverCarriesTheKeyInItsMessage()
    {
        foreach (var (status, body) in new[] { (HttpStatusCode.Unauthorized, FakeOmdb.Fixture("error_invalid_key.json")), (HttpStatusCode.Unauthorized, FakeOmdb.Fixture("error_limit_reached.json")),
                                               (HttpStatusCode.InternalServerError, "{}"), (HttpStatusCode.TooManyRequests, "{}") })
        {
            var fake = new FakeOmdb { FailWith = status, FailBody = body };
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Get(fake, "tt0133093"));
            Assert.DoesNotContain(FakeOmdb.TestKey, ex.ToString());
        }
    }

    [Fact]
    public async Task Search_ReturnsTheHits_WithAnId()
    {
        var hits = await FakeOmdb.Catalogue().Client().SearchAsync("matrix", "movie", CancellationToken.None);
        Assert.Contains(hits, h => h.ImdbId == "tt0133093" && h.Title == "The Matrix");
    }

    [Fact]
    public async Task ASearchWithNoHits_IsEmpty() =>
        Assert.Empty(await new FakeOmdb().RouteBody(_ => true, "{\"Response\":\"False\",\"Error\":\"Movie not found!\"}").Client().SearchAsync("zzz", null, CancellationToken.None));

    [Fact]
    public async Task Requests_ArePaced()
    {
        var fake = FakeOmdb.Catalogue();
        var delays = new List<TimeSpan>();
        var client = new OmdbClient(new HttpClient(fake), FakeOmdb.TestKey, TimeSpan.FromMilliseconds(250), delay: (span, _) => { delays.Add(span); return Task.CompletedTask; });

        await client.GetByIdAsync("tt0133093", true, CancellationToken.None);
        await client.GetByIdAsync("tt0903747", true, CancellationToken.None);

        Assert.Single(delays);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
    public async Task HealthCheck_IsTrueForARealAnswer_FalseForAFailure()
    {
        Assert.True(await FakeOmdb.Catalogue().Client().HealthCheckAsync(CancellationToken.None));
        Assert.False(await new FakeOmdb { FailWith = HttpStatusCode.Unauthorized, FailBody = FakeOmdb.Fixture("error_invalid_key.json") }.Client().HealthCheckAsync(CancellationToken.None));
        Assert.False(await new FakeOmdb().Client().HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public void ReadError_FindsAnErrorAnswer_AndOnlyThen()
    {
        Assert.Equal("Request limit reached!", OmdbClient.ReadError(FakeOmdb.Fixture("error_limit_reached.json")));
        Assert.Null(OmdbClient.ReadError(FakeOmdb.Fixture("movie_matrix.json")));
        Assert.Null(OmdbClient.ReadError("{\"Response\":\"True\",\"Plot\":\"an \\\"Error\\\" in the plot\"}"));
        Assert.Null(OmdbClient.ReadError("not json \"Error\""));
    }
}

public class MapperTests
{
    private static OmdbRecord Load(string fixture) => JsonSerializer.Deserialize<OmdbRecord>(FakeOmdb.Fixture(fixture))!;

    [Fact]
    public void AMovie_IsMappedCompletely()
    {
        var meta = MetadataMapper.Record(Load("movie_matrix.json"));

        Assert.Equal("tt0133093", meta.ExternalId);
        Assert.Equal("omdb", meta.Source);
        Assert.Equal("The Matrix", meta.Title);
        Assert.Equal(1999, meta.Year);
        Assert.Equal(136, meta.RuntimeMinutes);
        Assert.Equal(8.7, meta.Rating);
        Assert.Equal(["Action", "Sci-Fi"], meta.Genres);
        Assert.StartsWith("https://m.media-amazon.com/", meta.PosterUrl);
        Assert.Contains("Thomas A. Anderson", meta.Overview);
        Assert.Contains(meta.Cast, c => c.Name == "Keanu Reeves");
        Assert.Contains(meta.Crew, c => c.Name == "Lana Wachowski" && c.Job == "Director");
        Assert.Contains(meta.Crew, c => c.Job == "Writer");
    }

    [Fact]
    public void TheOtherRatings_AreKeptInExtendedData_NotInTheRatingField()
    {
        var data = MetadataMapper.Record(Load("movie_matrix.json")).ExtendedData!.Value;

        Assert.Equal(83, data.GetProperty("rotten_tomatoes_percent").GetDouble());
        Assert.Equal(73, data.GetProperty("metacritic_score").GetInt32());
        Assert.Equal(2284373, data.GetProperty("imdb_votes").GetInt32());
        Assert.Equal(3, data.GetProperty("ratings").GetArrayLength());
        Assert.Equal("R", data.GetProperty("rated").GetString());
        Assert.Equal("$177,559,005", data.GetProperty("box_office").GetString());
        Assert.Equal("tt0133093", data.GetProperty("ids").GetProperty("imdb").GetString());
        Assert.Contains("English", data.GetProperty("languages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1999-03-31", data.GetProperty("released_iso").GetString());
    }

    [Fact]
    public void ASeries_KeepsItsYearsAndSeasonCount()
    {
        var meta = MetadataMapper.Record(Load("series_breaking_bad.json"));

        Assert.Equal(2008, meta.Year);                                    // "2008–2013"
        Assert.Equal(49, meta.RuntimeMinutes);
        Assert.Equal(5, meta.ExtendedData!.Value.GetProperty("total_seasons").GetInt32());
        Assert.Equal("series", meta.ExtendedData!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void AnEpisode_KnowsItsSeasonEpisodeAndShow()
    {
        var meta = MetadataMapper.Record(Load("episode_pilot.json"));
        var data = meta.ExtendedData!.Value;

        Assert.Equal("Pilot", meta.Title);
        Assert.Equal(58, meta.RuntimeMinutes);
        Assert.Equal(1, data.GetProperty("season").GetInt32());
        Assert.Equal(1, data.GetProperty("episode").GetInt32());
        Assert.Equal("tt0903747", data.GetProperty("series_imdb_id").GetString());
        Assert.Equal("episode", data.GetProperty("type").GetString());
        Assert.Contains("Walter White", meta.Overview);
    }

    [Fact]
    public void NotAvailable_BecomesNothing_NotTheText_NA()
    {
        var meta = MetadataMapper.Record(Load("episode_day47_sparse.json"));
        var json = JsonSerializer.Serialize(meta);

        Assert.Null(meta.Overview);
        Assert.Null(meta.PosterUrl);
        Assert.Null(meta.RuntimeMinutes);
        Assert.Null(meta.Rating);
        Assert.Equal(4, meta.Cast.Count);                                   // the one thing this record has
        Assert.DoesNotContain("N/A", json);
        Assert.Equal(2008, meta.Year);
    }

    [Fact]
    public void AnUnnamedEpisode_KeepsIMDbsStandInTitle_ButItIsNeverOfferedAsAField()
    {
        // The mapper reports what OMDb says; the plugin declares no "title" field at episode level, so it is never used.
        var meta = MetadataMapper.Record(Load("episode_day47_sparse.json"));
        Assert.True(TextRules.IsPlaceholderTitle(meta.Title));

        var tv = new OmdbMetadataProvider().GetSupportedMediaTypes().Single(t => t.MediaTypeName == "tv");
        Assert.DoesNotContain("title", tv.LevelFields![2]);
    }

    [Fact]
    public void WriterCredits_AreKeptInTheJob()
    {
        var record = new OmdbRecord { ImdbId = "tt1234567", Title = "X", Writer = "Vince Gilligan (created by), Peter Gould (story, teleplay)", Director = "N/A" };
        var crew = MetadataMapper.Record(record).Crew;

        Assert.Equal(2, crew.Count);
        Assert.Contains(crew, c => c.Name == "Vince Gilligan" && c.Job == "Writer (created by)");
        Assert.Contains(crew, c => c.Name == "Peter Gould" && c.Job == "Writer (story, teleplay)");
    }

    [Fact]
    public void ASearchHit_IsMappedFromWhatTheSearchReturned()
    {
        var hit = new SearchHit { Title = "The Matrix", Year = "1999", ImdbId = "tt0133093", Type = "movie", Poster = "N/A" };
        var meta = MetadataMapper.FromSearch(hit);

        Assert.Equal("tt0133093", meta.ExternalId);
        Assert.Equal(1999, meta.Year);
        Assert.Null(meta.PosterUrl);
    }

    [Fact]
    public void NoOverview_NoPoster_IsNotInvented()
    {
        var meta = MetadataMapper.Record(new OmdbRecord { ImdbId = "tt1234567", Title = "X", Plot = "N/A", Poster = "N/A" });
        Assert.Null(meta.Overview);
        Assert.Null(meta.PosterUrl);
        Assert.Empty(meta.Genres);
    }
}
