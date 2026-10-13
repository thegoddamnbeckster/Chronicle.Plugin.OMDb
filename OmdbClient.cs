using System.Net;
using System.Text.Json;
using Chronicle.Plugins;
using Serilog;

namespace Chronicle.Plugin.OMDb;

/// <summary>
/// HTTP wrapper for OMDb (https://www.omdbapi.com). A free key allows 1,000 requests a day.
///
/// OMDb answers most problems with HTTP 200 and {"Response":"False","Error":"..."}, so the body is always read:
///   "Incorrect IMDb ID.", "Movie not found!", "Series not found!", "Error getting data." (OMDb's own database lacks a title IMDb
///   has: repeating the request gets the same answer, so it is a normal "not found")  -> null
///   "Invalid API key!" (HTTP 401)                                                    -> <see cref="PluginAuthException"/>
///   "Request limit reached!" (HTTP 401, the day's requests are used up)              -> HTTP 429
/// A 429 is thrown, never waited out: Chronicle then leaves the item Pending and stops the pass, and the rest is taken up on the next
/// run, instead of every remaining item being marked not found (the lesson of AniList and Deezer). Waiting for a day is not an option.
///
/// The key travels in the query string, as OMDb requires. It is never logged and never part of an exception message.
/// </summary>
internal sealed class OmdbClient : IDisposable
{
    public const string BaseUrl = "https://www.omdbapi.com/";
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    public const string PluginId = "chronicle.plugin.omdb";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] NotFoundErrors =
        ["Incorrect IMDb ID.", "Movie not found!", "Series not found!", "Episode not found!", "Error getting data.", "Season not found!", "Too many results."];

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>How many requests have been sent (the tests check each call's budget against this).</summary>
    public int Requests { get; private set; }

    public OmdbClient(HttpClient http, string apiKey, TimeSpan? interval = null, ILogger? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _apiKey = apiKey;
        _interval = interval ?? Interval;
        _log = log ?? Serilog.Core.Logger.None;
        _delay = delay ?? Task.Delay;
    }

    public static string UserAgent(string version) => $"Chronicle-OMDb/{version}";

    // ── requests ──────────────────────────────────────────────────────────────

    /// <summary>One title by IMDb id, with the full plot (or the short one).</summary>
    public async Task<OmdbRecord?> GetByIdAsync(string imdbId, bool fullPlot, CancellationToken ct) =>
        Found(await GetAsync<OmdbRecord>($"i={Uri.EscapeDataString(imdbId)}&plot={(fullPlot ? "full" : "short")}", ct).ConfigureAwait(false));

    /// <summary>The best single match for a title (and year, and type "movie" or "series"); OMDb does the matching.</summary>
    public async Task<OmdbRecord?> GetByTitleAsync(string title, int? year, string? type, bool fullPlot, CancellationToken ct) =>
        Found(await GetAsync<OmdbRecord>($"t={Uri.EscapeDataString(title)}" + (year is { } y ? $"&y={y}" : "") +
              (type is null ? "" : $"&type={type}") + $"&plot={(fullPlot ? "full" : "short")}", ct).ConfigureAwait(false));

    /// <summary>A list of titles matching a text (ten to a page): what Add Media and Fix match show.</summary>
    public async Task<List<SearchHit>> SearchAsync(string text, string? type, CancellationToken ct)
    {
        var response = await GetAsync<SearchResponse>($"s={Uri.EscapeDataString(text)}" + (type is null ? "" : $"&type={type}"), ct).ConfigureAwait(false);
        return response is { Search: { } hits } ? hits.Where(h => !string.IsNullOrEmpty(h.ImdbId)).ToList() : [];
    }

    private static OmdbRecord? Found(OmdbRecord? record) => record is { Ok: true } ? record : null;

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>Downloads one image. Only called when the application asks for the picture; never paced, never in bulk.</summary>
    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    // ── health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // The Matrix: a stable, small record.
            var record = await GetByIdAsync("tt0133093", fullPlot: false, ct).ConfigureAwait(false);
            return record is { Title.Length: > 0 };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("OMDb health check failed: {Reason}", ex.Message);
            return false;
        }
    }

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string query, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(query, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var error = ReadError(body);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (error is not null && error.Contains("limit", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("OMDb's request limit for today has been reached; the rest waits for the next run", null, HttpStatusCode.TooManyRequests);
            throw new PluginAuthException(PluginId, "OMDb rejected the API key" + (error is null ? "" : $" ({error})") +
                                                    ". Check the key in the plugin's settings (a new key must be activated from the email OMDb sends).");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("OMDb is rate limiting requests", null, HttpStatusCode.TooManyRequests);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OMDb answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        if (error is not null)
        {
            if (NotFoundErrors.Any(e => string.Equals(e, error, StringComparison.OrdinalIgnoreCase))) return null;
            if (error.Contains("limit", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("OMDb's request limit for today has been reached; the rest waits for the next run", null, HttpStatusCode.TooManyRequests);
            if (error.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase) || error.Contains("No API key", StringComparison.OrdinalIgnoreCase))
                throw new PluginAuthException(PluginId, $"OMDb rejected the API key ({error}).");
            // A query OMDb could not read (the plugin sent something it dislikes) must not fail the whole pass.
            _log.Warning("OMDb could not answer a request: {Error}", error);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("OMDb returned an answer this plugin could not read", ex, response.StatusCode);
        }
    }

    /// <summary>The error text of an answer ({"Response":"False","Error":"..."}), or null when it is not an error.</summary>
    internal static string? ReadError(string body)
    {
        if (!body.Contains("\"Error\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var failed = doc.RootElement.TryGetProperty("Response", out var r)
                         && string.Equals(r.GetString(), "False", StringComparison.OrdinalIgnoreCase);
            return failed && doc.RootElement.TryGetProperty("Error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string query, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _interval - (DateTimeOffset.UtcNow - _last);
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}?apikey={Uri.EscapeDataString(_apiKey)}&r=json&{query}");
            request.Headers.Accept.ParseAdd("application/json");
            Requests++;
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            return response;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
