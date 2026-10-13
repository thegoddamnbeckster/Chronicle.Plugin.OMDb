using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OMDb.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OMDB_LIVE") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OMDB_KEY")))
            Skip = "Set OMDB_LIVE=1 and OMDB_KEY to your free key to run against the real OMDb (a handful of the day's 1,000 requests).";
    }
}

/// <summary>
/// Runs only when OMDB_LIVE=1 and OMDB_KEY is set: talks to the real omdbapi.com to prove the queries, the field names and the
/// error handling work on today's data and not just on the recorded fixtures. The key is read from the environment and never stored.
/// </summary>
public class LiveTests
{
    private static OmdbMetadataProvider Real()
    {
        var provider = new OmdbMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_key"] = Environment.GetEnvironmentVariable("OMDB_KEY")! });
        return provider;
    }

    private static MediaSearchContext Item(string type, int level, string name, string imdb) =>
        new(name, HierarchyLevel: level, IsRealHierarchyPosition: true, MediaTypeName: type,
            KnownExternalIds: new Dictionary<string, string> { ["imdb"] = imdb });

    [LiveFact]
    public async Task HealthCheck_IsTrue() => Assert.True(await Real().HealthCheckAsync());

    [LiveFact]
    public async Task AMovie_AShow_AndAnEpisode_AreFoundById()
    {
        var provider = Real();

        var movie = (await provider.SearchAsync(Item("movies", 0, "The Matrix", "tt0133093"))).Single();
        var show = (await provider.SearchAsync(Item("tv", 0, "Breaking Bad", "tt0903747"))).Single();
        var episode = (await provider.SearchAsync(Item("tv", 2, "Pilot", "tt0959621"))).Single();

        Assert.Equal("The Matrix", movie.Metadata.Title);
        Assert.Equal("Breaking Bad", show.Metadata.Title);
        Assert.Equal("Pilot", episode.Metadata.Title);
        Assert.False(string.IsNullOrEmpty(episode.Metadata.Overview));
        Assert.NotNull(movie.Metadata.PosterUrl);
        Assert.True(movie.Metadata.Rating > 8);
    }

    [LiveFact]
    public async Task AnEpisodeThatOmdbHasNoPlotFor_IsStillFound_WithoutInventedText()
    {
        var results = await Real().SearchAsync(Item("tv", 2, "Day 47 Highlights", "tt1728310"));
        Assert.Single(results);
        Assert.Null(results[0].Metadata.Overview);
        Assert.DoesNotContain("N/A", System.Text.Json.JsonSerializer.Serialize(results[0].Metadata));
    }

    [LiveFact]
    public async Task ATitleOmdbLacks_IsNotFound() =>
        Assert.Empty(await Real().SearchAsync(Item("tv", 2, "Day 15", "tt14891408")));

    [LiveFact]
    public async Task AWrongKey_IsAnAuthFailure()
    {
        var provider = new OmdbMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_key"] = "notarealkey" });
        await Assert.ThrowsAsync<Chronicle.Plugins.PluginAuthException>(() => provider.GetByIdAsync("tt0133093"));
    }

    [LiveFact]
    public async Task TheSearchByName_FindsTheTitle_WhenTurnedOn()
    {
        var provider = new OmdbMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_key"] = Environment.GetEnvironmentVariable("OMDB_KEY")!, ["search_without_imdb_id"] = "true" });

        var results = await provider.SearchAsync(new MediaSearchContext("Breaking Bad", Year: 2008, HierarchyLevel: 0, IsRealHierarchyPosition: true, MediaTypeName: "tv"));

        Assert.True(results.Count == 1 && results[0].Score >= 90 && results[0].Metadata.ExternalId == "tt0903747");
    }
}
