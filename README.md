# Chronicle.Plugin.OMDb

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.OMDb?label=Chronicle.Plugin.OMDb&color=b8860b)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.OMDb/releases/latest)

Movie, series and episode metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches full plots, posters, runtimes, cast and crew, and ratings from IMDb, Rotten Tomatoes and Metacritic from [OMDb](https://www.omdbapi.com/), by IMDb id. It fills what the other sources leave blank, above all **TV episodes** (plots and pictures for episodes of reality, game and daily shows that TMDB and TVMaze have nothing for).

**Plugin ID:** `chronicle.plugin.omdb`
**Version:** 1.0.0
**Auth:** A free OMDb API key (1,000 requests a day), from [omdbapi.com/apikey.aspx](https://www.omdbapi.com/apikey.aspx)

Metadata provided by [OMDb](https://www.omdbapi.com/) (content licensed CC BY-NC 4.0: for personal, non-commercial use). OMDb is not endorsed by or affiliated with IMDb.

---

## Supported Media Types

| Media Type | Hierarchy | Fields |
|------------|-----------|--------|
| `movies`, `anime_movies` | Movie | title, year, overview, poster_url, runtime_minutes, genres, cast, crew, rating |
| `tv`, `anime` | Show / Season / Episode | show: the same; episode: year, overview, poster_url, runtime_minutes, cast, crew, rating. **Seasons are not offered** (OMDb has no season record). |

An episode never offers a **title**: IMDb names an episode nobody has named "Episode #9.54", and that must not replace your own title. Priority is 40 (below TMDB 10, IMDb and Last.fm 20), so OMDb fills what the others lack; reorder it per field in Metadata Assignment.

---

## External ID Format

Stored under source `omdb` as the bare IMDb id (`tt0133093`). Chronicle seeds OMDb from the IMDb ids other plugins found (it hands over `imdb:tt0133093`), so the plugin reads both, and an `omdb:` prefix.

**Fix match** accepts: a bare IMDb id, an id with an `imdb:` or `omdb:` prefix, or an imdb.com title URL. A person's id (`nm…`) is refused: OMDb knows titles.

The IMDb id is also published under `ids` for other plugins.

---

## Features

### Lookup by IMDb id

OMDb is asked for an item **by its IMDb id**, which the library's other sources usually hold: one request each and no matching to get wrong. The record's type is checked against the item's: a show's id on an episode, or an episode's on a movie, is refused rather than attached.

A title OMDb's own database lacks (it answers "Error getting data." for some titles IMDb has) is a normal "not found": asking again gets the same answer.

### Titles without an IMDb id

Skipped by default, so the free key's 1,000 requests a day go to exact lookups. Turn on **Search titles that have no IMDb id** to search movies and shows by name and year (one request each). OMDb's search is fuzzy, so a result is only accepted when its **title is the same** as the item's, and a year that disagrees pulls it under Chronicle's gate. Episodes are never searched by name: a name and a year cannot say which episode.

### What is kept

Plot, poster, runtime, genres, cast, directors and writers (with their credit, "Writer (created by)"), the IMDb rating. Everything else goes to the extended data, nothing dropped: **Rotten Tomatoes and Metacritic scores** (Chronicle has one rating field and it holds IMDb's), awards, box office, the age rating, languages, countries, votes, season and episode numbers, the show's IMDb id. OMDb's "N/A" is nothing, never text.

### The daily limit

The free key allows 1,000 requests a day. When they are used up OMDb answers HTTP 401 "Request limit reached!"; the plugin throws that as a **429**, so Chronicle leaves the item **Pending** and stops the pass, and the next run carries on. A first pass over thousands of items therefore takes several days; that is by design and happens in the background. A bad or unactivated key is reported as an authentication failure naming the fix (a new key must be activated from the link in OMDb's email). The key travels in the request as OMDb requires and is never logged or put in an error message.

---

## Setup

1. Request a free key at [omdbapi.com/apikey.aspx](https://www.omdbapi.com/apikey.aspx) and click the activation link in the email.
2. In Chronicle → **Plugins**, install this plugin from the catalog and enter the key in its settings.
3. Enable it. Use **Health check** to confirm the key works.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `api_key` | Yes | | Your free OMDb key. Stored encrypted. |
| `plot_length` | | `full` | `full` (several sentences) or `short`. |
| `search_without_imdb_id` | | `false` | Also search movies and shows that have no IMDb id, by name and year. |

---

## Background Tasks

| Task | Default schedule | Description |
|------|------------------|-------------|
| Fetch Missing OMDb Metadata | Daily 06:10, on | Looks up OMDb metadata, by IMDb id, for items not yet enriched by OMDb. Stops when the day's requests are used up and carries on next run |
| Re-sync All OMDb Metadata | Weekly Tuesday 06:10, off | Re-fetches everything. A request per item: several days on the free key |

---

## Repository Structure

```
Chronicle.Plugin.OMDb/
├── Chronicle.Plugin.OMDb.csproj
├── manifest.json
├── OmdbMetadataProvider.cs   # IMetadataProvider: routing by media type and level, search, fetch by id, settings
├── OmdbClient.cs             # HTTP client: error answers, daily limit, key handling
├── MetadataMapper.cs         # OMDb records -> Chronicle metadata
├── TextRules.cs              # "N/A", runtimes, votes, credits, placeholder episode titles
├── OmdbRef.cs                # understands IMDb ids and URLs
├── TtlCache.cs               # ten-minute memory of fetched records
├── OmdbModels.cs             # JSON models
└── tests/Chronicle.Plugin.OMDb.Tests/   # real recorded responses in Fixtures/
```

---

## Building

```powershell
dotnet build -c Release
```

Copy output to your Chronicle plugin directory:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.omdb"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.OMDb.dll"       $pluginDir
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.OMDb.deps.json" $pluginDir
Copy-Item "manifest.json"                                      $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle provides it, and Serilog too
> (referenced at the exact version the host loads, without bundling).

Tests:

```powershell
dotnet test tests/Chronicle.Plugin.OMDb.Tests                                              # recorded responses, no network
$env:OMDB_LIVE = "1"; $env:OMDB_KEY = "yourkey"; dotnet test tests/Chronicle.Plugin.OMDb.Tests   # also the real OMDb
dotnet test tests/Chronicle.Plugin.OMDb.Tests --collect:"XPlat Code Coverage"              # coverage
```

---

## Development

The plugin references `Chronicle.Plugins` via a local project reference:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

Both repositories must be cloned as siblings for this to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.OMDb\
```

---

## License

MIT. OMDb's data is CC BY-NC 4.0: personal, non-commercial use with attribution; see [omdbapi.com](https://www.omdbapi.com/).
