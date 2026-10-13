using System.Net;
using System.Text.Json;
using Chronicle.Plugin.OMDb;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OMDb.Tests;

public class ProviderTests
{
    private static OmdbMetadataProvider Provider(FakeOmdb fake, bool searchWithoutId = false) =>
        new(fake.Client(), fullPlot: true, searchWithoutId);

    private static MediaSearchContext Item(string type, int level, string name, string? imdb = null, int? year = null, string? known = "imdb") =>
        new(name, Year: year, HierarchyLevel: level, IsRealHierarchyPosition: true, MediaTypeName: type,
            KnownExternalIds: imdb is null ? null : new Dictionary<string, string> { [known ?? "imdb"] = imdb });

    // ── by id: the main path ──────────────────────────────────────────────────

    [Fact]
    public async Task AMovieWithAnImdbId_IsFetched_OneRequest_AsAnIdentifierMatch()
    {
        var fake = FakeOmdb.Catalogue();

        var results = await Provider(fake).SearchAsync(Item("movies", 0, "The Matrix", "tt0133093"));

        Assert.Single(results);
        Assert.Equal(100, results[0].Score);
        Assert.True(results[0].IdentifierMatch);
        Assert.Equal("tt0133093", results[0].Metadata.ExternalId);
        Assert.Equal(1, fake.Requests);
    }

    [Theory]
    [InlineData("tt0133093")]
    [InlineData("imdb:tt0133093")]      // the form the host seeds from other plugins
    [InlineData("https://www.imdb.com/title/tt0133093/")]
    public async Task TheIdIsReadInEveryFormTheHostHandsOver(string stored)
    {
        var results = await Provider(FakeOmdb.Catalogue()).SearchAsync(Item("movies", 0, "The Matrix", stored));
        Assert.Equal("tt0133093", results.Single().Metadata.ExternalId);
    }

    [Fact]
    public async Task AnEpisodeWithItsOwnImdbId_IsFetched_ForItsPlotAndPicture()
    {
        var fake = FakeOmdb.Catalogue();
        var results = await Provider(fake).SearchAsync(Item("tv", 2, "Episode 1", "tt0959621"));

        Assert.Equal(100, results.Single().Score);
        Assert.Contains("Walter White", results.Single().Metadata.Overview);
        Assert.NotNull(results.Single().Metadata.PosterUrl);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task AShowWithAnImdbId_IsFetched()
    {
        var results = await Provider(FakeOmdb.Catalogue()).SearchAsync(Item("tv", 0, "Breaking Bad", "tt0903747"));
        Assert.Equal("Breaking Bad", results.Single().Metadata.Title);
    }

    [Fact]
    public async Task OmdbsOwnStoredId_IsPreferred()
    {
        var context = Item("movies", 0, "The Matrix") with
        {
            KnownExternalIds = new Dictionary<string, string> { ["imdb"] = "tt0903747", ["omdb"] = "tt0133093" },
        };
        var results = await Provider(FakeOmdb.Catalogue()).SearchAsync(context);
        Assert.Equal("tt0133093", results.Single().Metadata.ExternalId);
    }

    // ── a record of the wrong kind is never used ──────────────────────────────

    [Theory]
    [InlineData("movies", 0, "tt0903747")]    // a series id on a movie
    [InlineData("movies", 0, "tt0959621")]    // an episode id on a movie
    [InlineData("tv", 0, "tt0133093")]        // a movie id on a show
    [InlineData("tv", 0, "tt0959621")]        // an episode id on a show
    [InlineData("tv", 2, "tt0903747")]        // a show id on an episode
    [InlineData("tv", 2, "tt0133093")]        // a movie id on an episode
    [InlineData("anime", 2, "tt0903747")]
    public async Task AnIdOfTheWrongKind_IsRefused(string type, int level, string imdb)
    {
        var results = await Provider(FakeOmdb.Catalogue()).SearchAsync(Item(type, level, "Whatever", imdb));
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("anime_movies", 0, "tt0133093")]
    [InlineData("anime", 0, "tt0903747")]
    [InlineData("anime", 2, "tt0959621")]
    public async Task TheAnimeTypes_FollowTheirFamily(string type, int level, string imdb) =>
        Assert.Single(await Provider(FakeOmdb.Catalogue()).SearchAsync(Item(type, level, "X", imdb)));

    // ── what is not looked up ─────────────────────────────────────────────────

    [Fact]
    public async Task ASeason_IsNeverLookedUp_AndCostsNothing()
    {
        var fake = FakeOmdb.Catalogue();
        var results = await Provider(fake).SearchAsync(Item("tv", 1, "Season 1", "tt0903747"));

        Assert.Empty(results);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AMoviesCollectionContainer_IsNeverLookedUp()
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Empty(await Provider(fake).SearchAsync(Item("movies", 1, "The Matrix Collection")));
        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("books")]
    [InlineData("audiobooks")]
    [InlineData("people")]
    public async Task OtherMediaTypes_AreNotSearched_AtAll(string type)
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Empty(await Provider(fake).SearchAsync(Item(type, 0, "The Matrix", "tt0133093")));
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AnItemWithNoImdbId_IsSkipped_ByDefault_SoTheFreeQuotaGoesToExactLookups()
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Empty(await Provider(fake).SearchAsync(Item("movies", 0, "The Matrix", year: 1999)));
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AnIdOmdbDoesNotHave_GivesNoCandidate_NotAWrongOne()
    {
        var results = await Provider(FakeOmdb.Catalogue()).SearchAsync(Item("tv", 2, "Day 15", "tt14891408"));
        Assert.Empty(results);
    }

    // ── the opt-in search by name ─────────────────────────────────────────────

    [Fact]
    public async Task WithTheSearchTurnedOn_AShowIsFoundByNameAndYear_InOneRequest()
    {
        var fake = FakeOmdb.Catalogue();
        var results = await Provider(fake, searchWithoutId: true).SearchAsync(Item("tv", 0, "Breaking Bad", year: 2008));

        Assert.Equal(1, fake.Requests);
        Assert.Contains("t=Breaking Bad", fake.Queries[0]);
        Assert.Contains("y=2008", fake.Queries[0]);
        Assert.Contains("type=series", fake.Queries[0]);
        Assert.True(results.Single().Score >= 90);
    }

    [Fact]
    public async Task ASearchResultOfADifferentTitle_IsNotAccepted()
    {
        // OMDb's t= search is fuzzy: it may return something else entirely. Only the same title counts.
        var fake = new FakeOmdb().Route(q => q.Contains("t="), "movie_matrix.json");
        var results = await Provider(fake, searchWithoutId: true).SearchAsync(Item("movies", 0, "Totally Different Film", year: 1999));
        Assert.Empty(results);
    }

    [Fact]
    public async Task ASearchResultOfTheSameTitleButAnotherDecade_ScoresBelowTheGate()
    {
        var fake = new FakeOmdb().Route(q => q.Contains("t="), "movie_matrix.json");
        var results = await Provider(fake, searchWithoutId: true).SearchAsync(Item("movies", 0, "The Matrix", year: 1950));
        Assert.True(results.Count == 0 || results[0].Score < 50);
    }

    [Fact]
    public async Task AnEpisode_IsNeverSearchedByName_EvenWithTheSearchOn()
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Empty(await Provider(fake, searchWithoutId: true).SearchAsync(Item("tv", 2, "Pilot", year: 2008)));
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task ATitleNotFoundByName_IsNotFound()
    {
        var results = await Provider(FakeOmdb.Catalogue(), searchWithoutId: true).SearchAsync(Item("movies", 0, "zzzzqqqxxnotamovie"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task TheLibrarysWording_IsKeptAsAnAlias()
    {
        var fake = FakeOmdb.Catalogue();
        var results = await Provider(fake, searchWithoutId: true).SearchAsync(Item("tv", 0, "The Breaking Bad (2008)", year: 2008));
        Assert.Contains("The Breaking Bad", results.Single().Metadata.AlternateNames);
    }

    [Fact]
    public async Task FreeText_ListsTheTitlesFound_AndCapsNothing()
    {
        var fake = FakeOmdb.Catalogue();
        var results = await Provider(fake).SearchAsync(new MediaSearchContext("matrix"));   // typed into Add Media: no hierarchy position

        Assert.Equal(1, fake.Requests);
        Assert.Contains(results, r => r.Metadata.ExternalId == "tt0133093");
        Assert.All(results, r => Assert.StartsWith("tt", r.Metadata.ExternalId));
    }

    [Fact]
    public async Task FreeTextWithNoTitle_DoesNothing()
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Empty(await Provider(fake).SearchAsync(new MediaSearchContext("  ")));
        Assert.Equal(0, fake.Requests);
    }

    // ── errors ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDaysQuotaUsedUp_DuringASearch_IsA429_SoTheItemStaysPending()
    {
        var fake = new FakeOmdb { FailWith = HttpStatusCode.Unauthorized, FailBody = FakeOmdb.Fixture("error_limit_reached.json") };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).SearchAsync(Item("movies", 0, "The Matrix", "tt0133093")));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task AnInvalidKey_DuringASearch_IsAnAuthFailure()
    {
        var fake = new FakeOmdb { FailWith = HttpStatusCode.Unauthorized, FailBody = FakeOmdb.Fixture("error_invalid_key.json") };
        await Assert.ThrowsAsync<PluginAuthException>(() => Provider(fake).SearchAsync(Item("movies", 0, "The Matrix", "tt0133093")));
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tt0133093", "The Matrix")]
    [InlineData("imdb:tt0903747", "Breaking Bad")]
    [InlineData("omdb:tt0959621", "Pilot")]
    [InlineData("https://www.imdb.com/title/tt0133093/", "The Matrix")]
    public async Task GetById_ReadsEveryForm(string reference, string title) =>
        Assert.Equal(title, (await Provider(FakeOmdb.Catalogue()).GetByIdAsync(reference)).Title);

    [Theory]
    [InlineData("nm0000206")]
    [InlineData("27")]
    [InlineData("")]
    public async Task AnIdThatCannotBeRead_IsEmpty_WithoutARequest(string reference)
    {
        var fake = FakeOmdb.Catalogue();
        Assert.Equal("", (await Provider(fake).GetByIdAsync(reference)).Title);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task AnIdOmdbHasNoDataFor_IsEmpty_NotAnError() =>
        Assert.Equal("", (await Provider(FakeOmdb.Catalogue()).GetByIdAsync("tt14891408")).Title);

    [Fact]
    public async Task WhatWasJustFetched_IsRemembered()
    {
        var fake = FakeOmdb.Catalogue();
        var provider = Provider(fake);

        await provider.SearchAsync(Item("movies", 0, "The Matrix", "tt0133093"));
        await provider.GetByIdAsync("tt0133093");          // the host asks for the id it just chose
        await provider.GetByIdAsync("imdb:tt0133093");

        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task NoCallEverMakesMoreThanTwoRequests()
    {
        foreach (var (context, searchOn) in new[]
        {
            (Item("movies", 0, "The Matrix", "tt0133093"), false), (Item("tv", 2, "Ep", "tt0959621"), false), (Item("tv", 0, "Breaking Bad", year: 2008), true),
            (Item("tv", 1, "Season 1", "tt0903747"), false), (new MediaSearchContext("matrix"), false),
        })
        {
            var fake = FakeOmdb.Catalogue();
            await Provider(fake, searchOn).SearchAsync(context);
            Assert.True(fake.Requests <= 2, $"{context.Name}: {fake.Requests}");
        }
    }

    // ── images and health ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://m.media-amazon.com/images/M/MV5B.jpg", true)]
    [InlineData("https://ia.media-imdb.com/images/M/x.jpg", true)]
    [InlineData("https://img.omdbapi.com/?i=tt0133093&apikey=x", true)]
    [InlineData("http://m.media-amazon.com/images/M/x.jpg", false)]
    [InlineData("https://evil.example.com/media-amazon.com/x.jpg", false)]
    [InlineData("https://notmedia-amazon.com/x.jpg", false)]
    [InlineData("file:///c:/secrets.txt", false)]
    [InlineData("not a url", false)]
    public async Task OnlyTheHostsOmdbPostersLiveOn_AreFetched(string url, bool allowed)
    {
        var provider = Provider(new FakeOmdb { ImageBytes = [1, 2, 3] });
        if (allowed) Assert.Equal([1, 2, 3], await provider.GetImageAsync(url));
        else await Assert.ThrowsAsync<ArgumentException>(() => provider.GetImageAsync(url));
    }

    [Fact]
    public async Task Health_IsTrueWhenOmdbAnswers_AndFalseWhenNotConfigured()
    {
        Assert.True(await Provider(FakeOmdb.Catalogue()).HealthCheckAsync());
        Assert.False(await new OmdbMetadataProvider().HealthCheckAsync());
    }

    [Fact]
    public async Task AnUnconfiguredPlugin_RefusesToSearch()
    {
        var provider = new OmdbMetadataProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Item("movies", 0, "The Matrix", "tt0133093")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetByIdAsync("tt0133093"));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(70)]
    public void ScoreTitle_NeedsTheSameTitle(int _)
    {
        var record = new OmdbRecord { Title = "Breaking Bad", Year = "2008–2013" };
        Assert.Equal(95, OmdbMetadataProvider.ScoreTitle("Breaking Bad", 2008, record));
        Assert.Equal(80, OmdbMetadataProvider.ScoreTitle("Breaking Bad", 2009, record));
        Assert.Equal(30, OmdbMetadataProvider.ScoreTitle("Breaking Bad", 1990, record));
        Assert.Equal(60, OmdbMetadataProvider.ScoreTitle("Breaking Bad", null, record));
        Assert.Equal(0, OmdbMetadataProvider.ScoreTitle("Better Call Saul", 2008, record));
    }
}
