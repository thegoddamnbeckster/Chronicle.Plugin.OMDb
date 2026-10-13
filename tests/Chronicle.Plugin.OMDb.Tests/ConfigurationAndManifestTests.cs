using System.Text.Json;
using Chronicle.Plugin.OMDb;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.OMDb.Tests;

public class ConfigurationTests
{
    [Fact]
    public void ConfiguringWithNoKey_SaysWhereToGetOne_SoTheUserSeesWhyItIsOff()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OmdbMetadataProvider().Configure(new Dictionary<string, string>()));
        Assert.Contains("api_key", ex.Message);
        Assert.Contains("omdbapi.com", ex.Message);
        Assert.Throws<InvalidOperationException>(() => new OmdbMetadataProvider().Configure(new Dictionary<string, string> { ["api_key"] = "   " }));
    }

    [Fact]
    public void ConfiguringWithAKey_NamesThePluginInTheUserAgent_AndNeverEchoesTheKey()
    {
        var provider = new OmdbMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_key"] = "abc12345" });

        Assert.Equal("Chronicle-OMDb/1.0.1", provider.ConfiguredUserAgent);
        Assert.DoesNotContain("abc12345", provider.ConfiguredUserAgent);
        provider.Dispose();
    }

    [Theory]
    [InlineData("episodes", true)]
    [InlineData("all", false)]
    [InlineData("ALL", false)]
    [InlineData("", true)]
    [InlineData("nonsense", true)]
    public void TheScope_IsEpisodesOnlyUnlessEverythingIsChosen(string value, bool episodesOnly)
    {
        var provider = new OmdbMetadataProvider(new FakeOmdb().Client());
        provider.Configure(new Dictionary<string, string> { ["scope"] = value });
        Assert.Equal(episodesOnly, provider.EpisodesOnly);
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("short", false)]
    [InlineData("SHORT", false)]
    [InlineData("", true)]
    [InlineData("nonsense", true)]
    public void ThePlotLength_IsTheOneChosen(string value, bool full)
    {
        var provider = new OmdbMetadataProvider(new FakeOmdb().Client());
        provider.Configure(new Dictionary<string, string> { ["plot_length"] = value });
        Assert.Equal(full, provider.FullPlot);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    public void SearchingTitlesWithoutAnId_IsOffUnlessTheUserTurnedItOn(string value, bool expected)
    {
        var provider = new OmdbMetadataProvider(new FakeOmdb().Client());
        provider.Configure(new Dictionary<string, string> { ["search_without_imdb_id"] = value });
        Assert.Equal(expected, provider.SearchWithoutId);
    }

    [Fact]
    public void TheSettingsForm_HasTheKeyAsARequiredPassword_AndSensibleDefaults()
    {
        var schema = new OmdbMetadataProvider().GetSettingsSchema().Settings;

        Assert.Equal(["api_key", "scope", "plot_length", "search_without_imdb_id"], schema.Select(s => s.Key).ToArray());
        Assert.Equal("episodes", schema.Single(s => s.Key == "scope").DefaultValue);
        Assert.Equal(["episodes", "all"], schema.Single(s => s.Key == "scope").Options.Select(o => o.Value).ToArray());
        var key = schema.Single(s => s.Key == "api_key");
        Assert.Equal(SettingType.Password, key.Type);
        Assert.True(key.Required);
        Assert.Contains("omdbapi.com/apikey.aspx", key.Description);
        Assert.Equal("full", schema.Single(s => s.Key == "plot_length").DefaultValue);
        Assert.Equal("false", schema.Single(s => s.Key == "search_without_imdb_id").DefaultValue);
        Assert.Equal(SettingType.Boolean, schema.Single(s => s.Key == "search_without_imdb_id").Type);
    }

    [Fact]
    public void ItAcceptsTheImdbCrossReferencePrefix() =>
        Assert.Equal(["imdb:"], new OmdbMetadataProvider().GetAcceptedCrossRefPrefixes().ToArray());
}

public class CacheAndManifestTests
{
    [Fact]
    public void AnEntry_IsRemembered_UntilItExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new TtlCache<string>(TimeSpan.FromMinutes(10), clock: () => now);
        cache.Set("a", "one");

        Assert.True(cache.TryGet("a", out var fresh));
        Assert.Equal("one", fresh);
        now += TimeSpan.FromMinutes(11);
        Assert.False(cache.TryGet("a", out _));
    }

    [Fact]
    public void TheCache_CanBeEmptied()
    {
        var cache = new TtlCache<int>(TimeSpan.FromMinutes(1));
        cache.Set("a", 1);
        cache.Clear();
        Assert.False(cache.TryGet("a", out _));
    }

    private static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement;

    [Fact]
    public void TheManifest_MatchesTheCode()
    {
        var provider = new OmdbMetadataProvider();
        var manifest = Manifest();

        Assert.Equal(provider.PluginId, manifest.GetProperty("plugin_id").GetString());
        Assert.Equal(provider.Name, manifest.GetProperty("name").GetString());
        Assert.Equal(provider.Version, manifest.GetProperty("version").GetString());
        Assert.Equal(typeof(OmdbMetadataProvider).FullName, manifest.GetProperty("entry_type").GetString());
        Assert.Equal(provider.GetSupportedMediaTypes().Select(t => t.MediaTypeName).Order().ToArray(),
            manifest.GetProperty("supported_media_types").EnumerateArray().Select(e => e.GetString()!).Order().ToArray());
    }

    [Fact]
    public void TheProjectVersion_IsTheCodesVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Chronicle.Plugin.OMDb.csproj"));
        Assert.Contains($"<Version>{new OmdbMetadataProvider().Version}</Version>", csproj);
    }

    [Fact]
    public void TheManifestTasks_AreTheHostsBuiltInHandlers()
    {
        var tasks = Manifest().GetProperty("background_tasks").EnumerateArray().ToList();

        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks.Select(t => t.GetProperty("task_id").GetString()).ToArray());
        Assert.True(tasks[0].GetProperty("default_enabled").GetBoolean());
        Assert.False(tasks[1].GetProperty("default_enabled").GetBoolean());   // a full re-fetch costs a request per item: opt-in
        Assert.All(tasks, t => Assert.Equal(5, t.GetProperty("default_cron").GetString()!.Split(' ').Length));
    }

    [Fact]
    public void TheFixMatchHint_NamesTheFormsTheParserAccepts()
    {
        var hint = Manifest().GetProperty("fixMatchHint").GetString()!;
        foreach (var form in new[] { "tt0133093", "imdb.com", "imdb:", "omdb:" }) Assert.Contains(form, hint);
    }

    [Fact]
    public void TheTypes_AreDeclaredLikeTheOtherPluginsDeclareThem()
    {
        var types = new OmdbMetadataProvider().GetSupportedMediaTypes();

        Assert.Equal(["movies", "anime_movies", "tv", "anime"], types.Select(t => t.MediaTypeName).ToArray());
        Assert.All(types, t => Assert.Equal(40, t.DefaultPriority));   // below TMDB (10), IMDb and Last.fm (20): it fills gaps
        Assert.All(types.Where(t => t.MediaTypeName is "tv" or "anime"), t =>
        {
            Assert.Equal(3, t.HierarchyLevels);
            Assert.Equal(["Show", "Season", "Episode"], t.HierarchyLabels);
        });
        Assert.All(types.Where(t => t.MediaTypeName is "movies" or "anime_movies"), t => Assert.Equal(1, t.HierarchyLevels));
    }

    [Fact]
    public void SeasonsAreDeclaredWithNoFields_SoTheHostDropsThoseRowsInsteadOfQueueingThem()
    {
        foreach (var type in new OmdbMetadataProvider().GetSupportedMediaTypes().Where(t => t.MediaTypeName is "tv" or "anime"))
        {
            Assert.Empty(type.LevelFields![1]);        // OMDb has no season record
            Assert.NotEmpty(type.LevelFields[2]);
            Assert.DoesNotContain("title", type.LevelFields[2]);   // IMDb's "Episode #9.54" must never be offered as a title
            Assert.Contains("overview", type.LevelFields[2]);
            Assert.Contains("poster_url", type.LevelFields[2]);
        }
    }
}
