using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.OMDb;

/// <summary>
/// Chronicle metadata provider for OMDb (movies, shows and episodes by IMDb id). Needs a free OMDb API key.
///
/// OMDb is looked up by IMDb id, which the library's other sources already hold, so there is no matching to get wrong: a title that
/// carries an IMDb id is fetched, one request each, and the record's type is checked against the item's (an episode id on a show is
/// refused). A title with no id is searched for only when the user turns that on, because the free key allows 1,000 requests a day.
///
///   movies, anime_movies  level 0                       a movie
///   tv, anime             level 0 show, level 2 episode  seasons are not offered (OMDb has no season record): level 1 is declared
///                                                        with no fields, so the host drops those rows instead of queueing them
///
/// External ids (source "omdb") are bare IMDb ids (tt0133093). The host seeds rows from other plugins' IMDb ids (it hands over
/// "imdb:tt…"), so both forms are read. Fix match also takes an imdb.com title URL.
///
/// Every call makes at most two requests (Chronicle allows 25 seconds); see <see cref="OmdbClient"/> for the error and quota contract.
/// </summary>
public sealed class OmdbMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => "chronicle.plugin.omdb";
    public string Name     => "OMDb";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    public const string KeyApiKey = "api_key";
    public const string KeyPlotLength = "plot_length";
    public const string KeySearchWithoutId = "search_without_imdb_id";

    private static readonly ILogger Log = Serilog.Log.ForContext<OmdbMetadataProvider>();

    private OmdbClient? _client;
    private HttpClient? _ownedHttp;
    private bool _clientSupplied;
    private bool _fullPlot = true;
    private bool _searchWithoutId;

    internal bool FullPlot => _fullPlot;
    internal bool SearchWithoutId => _searchWithoutId;
    internal string? ConfiguredUserAgent { get; private set; }

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly TtlCache<OmdbRecord> _records = new(CacheFor, 2000);

    private const int ConfidentScore = 70;

    public OmdbMetadataProvider() { }

    internal OmdbMetadataProvider(OmdbClient client, bool fullPlot = true, bool searchWithoutId = false)
    {
        _client = client;
        _clientSupplied = true;
        _fullPlot = fullPlot;
        _searchWithoutId = searchWithoutId;
    }

    // ── capabilities ──────────────────────────────────────────────────────────

    // "title" is offered for movies and shows but not for episodes: IMDb names the episodes nobody has named "Episode #9.54", and
    // that must never be offered as a title over the library's own. What an episode gains from OMDb is its plot, picture, length,
    // people and rating.
    private static readonly List<string> TitleFields =
        ["title", "year", "overview", "poster_url", "runtime_minutes", "genres", "cast", "crew", "rating"];

    private static readonly List<string> EpisodeFields =
        ["year", "overview", "poster_url", "runtime_minutes", "cast", "crew", "rating"];

    // Lowest priority of the metadata plugins (TMDB 10, IMDb and Last.fm 20): OMDb fills what the others lack, and Metadata
    // Assignment can move it up per field.
    private const int Priority = 40;

    private static readonly MediaTypeSupport[] SupportedTypes =
    [
        new() { MediaTypeName = "movies", DisplayName = "Movies", HierarchyLevels = 1, DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        new() { MediaTypeName = "anime_movies", DisplayName = "Anime Movies", HierarchyLevels = 1, DefaultPriority = Priority, SupportedFields = [.. TitleFields] },
        new()
        {
            MediaTypeName = "tv", DisplayName = "TV", HierarchyLevels = 3, HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = Priority, SupportedFields = [.. TitleFields],
            LevelFields = new Dictionary<int, List<string>> { [1] = [], [2] = [.. EpisodeFields] },
        },
        new()
        {
            MediaTypeName = "anime", DisplayName = "Anime", HierarchyLevels = 3, HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = Priority, SupportedFields = [.. TitleFields],
            LevelFields = new Dictionary<int, List<string>> { [1] = [], [2] = [.. EpisodeFields] },
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => SupportedTypes;

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["imdb:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = KeyApiKey, Label = "OMDb API key", Type = SettingType.Password, Required = true,
                Description = "A free key from https://www.omdbapi.com/apikey.aspx (1,000 requests a day). OMDb emails it; the link in " +
                              "that email must be clicked to activate it. Stored encrypted.",
            },
            new SettingDefinition
            {
                Key = KeyPlotLength, Label = "Plot length", Type = SettingType.Dropdown, DefaultValue = "full",
                Description = "Full plots are several sentences; short ones are a sentence or two.",
                Options = [new SelectOption { Value = "full", Label = "Full" }, new SelectOption { Value = "short", Label = "Short" }],
            },
            new SettingDefinition
            {
                Key = KeySearchWithoutId, Label = "Search titles that have no IMDb id", Type = SettingType.Boolean, DefaultValue = "false",
                Description = "OMDb is looked up by IMDb id, which your other sources usually supply. Titles without one are skipped by " +
                              "default so the free key's 1,000 requests a day go to exact lookups. Turn this on to search movies and " +
                              "shows by name and year as well (one request each; episodes are never searched by name).",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        string? Get(string key) => settings.TryGetValue(key, out var v) ? v?.Trim() : null;

        _fullPlot = !string.Equals(Get(KeyPlotLength), "short", StringComparison.OrdinalIgnoreCase);
        _searchWithoutId = bool.TryParse(Get(KeySearchWithoutId), out var search) && search;

        if (_clientSupplied) return;   // a client was supplied directly (tests)

        var key = Get(KeyApiKey);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OMDb plugin requires 'api_key' to be configured (a free key from omdbapi.com/apikey.aspx).");

        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        ConfiguredUserAgent = OmdbClient.UserAgent(Version);
        _ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(ConfiguredUserAgent);
        _client = new OmdbClient(_ownedHttp, key, OmdbClient.Interval, Log);
        _records.Clear();
        Log.Information("OMDb configured ({Plot} plots, {Search})", _fullPlot ? "full" : "short",
            _searchWithoutId ? "titles without an IMDb id are searched" : "lookups by IMDb id only");
    }

    // ── search ────────────────────────────────────────────────────────────────

    private enum Family { None, Movie, Series }

    private static Family FamilyOf(string? mediaTypeName) => mediaTypeName?.ToLowerInvariant() switch
    {
        "movies" or "movie" or "anime_movies" => Family.Movie,
        "tv" or "anime" => Family.Series,
        _ => Family.None,
    };

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        // A null type means the caller did not say: treat it as a movie or a show, never as music or a book.
        var family = context.MediaTypeName is null ? Family.Movie : FamilyOf(context.MediaTypeName);
        if (family == Family.None) return [];

        var titles = SearchTitles(context);

        // Free text (Add Media): the user typed something that may be a movie or a show.
        var isFreeSearch = context.HierarchyLevel == 0 && !context.IsRealHierarchyPosition && context.ParentName is null;
        if (isFreeSearch)
            return titles.Count == 0 ? [] : await FreeSearchAsync(titles[0], context.MediaTypeName is null ? null : family, ct).ConfigureAwait(false);

        // Seasons have no OMDb record; a movie's collection (level 1+) is not a title.
        if (!LevelIsOffered(family, context.HierarchyLevel)) return [];

        // An IMDb id the item already holds is the surest match, and the only one that costs nothing in guessing.
        if (await KnownIdentifierAsync(context, family, ct).ConfigureAwait(false) is { } known) return [known];

        // No id: only when the user asked for searches, and never for an episode (a name and a year cannot say which episode).
        if (!_searchWithoutId || titles.Count == 0) return [];
        if (family == Family.Series && context.HierarchyLevel != 0) return [];

        return await SearchByTitleAsync(titles, family, context.Year, ct).ConfigureAwait(false);
    }

    private static bool LevelIsOffered(Family family, int level) => family == Family.Movie ? level == 0 : level is 0 or 2;

    /// <summary>The names an item is searched under: its precise name, then its other forms, each without a "(YYYY)" suffix.</summary>
    private static List<string> SearchTitles(MediaSearchContext context)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.PreciseName)) all.Add(context.PreciseName);
        all.AddRange(context.AltTitles ?? []);
        all.Add(context.Name);
        return all.Select(TextRules.StripYearSuffix).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
    }

    /// <summary>Whether a record can be the item: a movie for a movie, a show for a show, an episode for an episode.</summary>
    private static bool TypeFits(Family family, int level, string? type)
    {
        var t = TextRules.Value(type)?.ToLowerInvariant();
        if (family == Family.Movie) return t is "movie" or null;
        return level == 2 ? t == "episode" : t == "series";
    }

    /// <summary>An IMDb id the item already holds under "omdb" (this plugin's own) or "imdb" (what every other plugin writes).</summary>
    private async Task<ScoredCandidate?> KnownIdentifierAsync(MediaSearchContext context, Family family, CancellationToken ct)
    {
        var ids = context.KnownExternalIds;
        if (ids is null) return null;
        var text = ids.TryGetValue("omdb", out var own) ? own : ids.TryGetValue("imdb", out var imdb) ? imdb : null;
        if (OmdbRef.Parse(text) is not { } reference) return null;

        var record = await RecordAsync(reference.ImdbId, ct).ConfigureAwait(false);
        if (record is null || !TypeFits(family, context.HierarchyLevel, record.Type)) return null;
        return new ScoredCandidate(MetadataMapper.Record(record), 100, "known IMDb id") { IdentifierMatch = true };
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchByTitleAsync(List<string> titles, Family family, int? year, CancellationToken ct)
    {
        var type = family == Family.Movie ? "movie" : "series";
        foreach (var title in titles)
        {
            var record = await _client!.GetByTitleAsync(title, year, type, _fullPlot, ct).ConfigureAwait(false);
            if (record is null || !TypeFits(family, 0, record.Type)) continue;
            _records.Set(TextRules.Value(record.ImdbId) ?? "", record);

            var score = ScoreTitle(TextRules.StripYearSuffix(title), year, record);
            if (score <= 0) continue;
            return [new ScoredCandidate(WithAlias(MetadataMapper.Record(record), title, score >= 50), score, "title search")];
        }
        return [];
    }

    /// <summary>How well OMDb's best match fits the item: the title must be the same, and the year agrees or does not.</summary>
    internal static int ScoreTitle(string title, int? year, OmdbRecord record)
    {
        if (TextRules.Normalise(title) != TextRules.Normalise(record.Title)) return 0;
        var score = 60;
        if (year is { } y && TextRules.YearOf(record.Year) is { } found)
            score += Math.Abs(found - y) switch { 0 => 35, 1 => 20, _ => -30 };
        return Math.Clamp(score, 0, 99);
    }

    private async Task<IReadOnlyList<ScoredCandidate>> FreeSearchAsync(string text, Family? family, CancellationToken ct)
    {
        var type = family switch { Family.Movie => "movie", Family.Series => "series", _ => null };
        var hits = await _client!.SearchAsync(text, type, ct).ConfigureAwait(false);
        var typed = TextRules.Normalise(text);
        return hits
            .Select(h => (Hit: h, Score: TextRules.Normalise(h.Title) == typed ? 90 : TextRules.Normalise(h.Title).Contains(typed, StringComparison.Ordinal) ? 55 : 40))
            .OrderByDescending(x => x.Score)
            .Take(10)
            .Select(x => new ScoredCandidate(MetadataMapper.FromSearch(x.Hit), x.Score, "title search"))
            .ToList();
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (OmdbRef.Parse(externalId) is not { } reference) return Empty(externalId);
        var record = await RecordAsync(reference.ImdbId, ct).ConfigureAwait(false);
        return record is null ? Empty(reference.ImdbId) : MetadataMapper.Record(record);
    }

    private async Task<OmdbRecord?> RecordAsync(string imdbId, CancellationToken ct)
    {
        if (_records.TryGet(imdbId, out var cached)) return cached;
        var record = await _client!.GetByIdAsync(imdbId, _fullPlot, ct).ConfigureAwait(false);
        if (record is not null) _records.Set(imdbId, record);
        return record;
    }

    private static MediaMetadata Empty(string externalId) => new() { ExternalId = externalId, Source = MetadataMapper.Source };

    /// <summary>The library's own wording of a matched title is another name for it: Chronicle's name check accepts a candidate under
    /// any of its alternate names.</summary>
    private static MediaMetadata WithAlias(MediaMetadata metadata, string wording, bool matched)
    {
        wording = TextRules.StripYearSuffix(wording);
        if (matched && wording.Length > 0
            && !string.Equals(wording, metadata.Title, StringComparison.OrdinalIgnoreCase)
            && !metadata.AlternateNames.Contains(wording, StringComparer.OrdinalIgnoreCase))
            metadata.AlternateNames.Add(wording);
        return metadata;
    }

    // ── images and health ─────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        // Only the hosts OMDb's own poster addresses point at: this plugin is asked for pictures it handed out, nothing else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".media-amazon.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "media-amazon.com"
                 || uri.Host.EndsWith(".media-imdb.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host == "img.omdbapi.com"))
            throw new ArgumentException("OMDb pictures come from media-amazon.com, media-imdb.com and img.omdbapi.com.", nameof(url));
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            Log.Warning("OMDb health check skipped: plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_clientSupplied) _client?.Dispose();
        _ownedHttp?.Dispose();
        _client = null;
        _ownedHttp = null;
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("OMDb plugin is not configured. Call Configure() with an api_key first.");
    }
}
