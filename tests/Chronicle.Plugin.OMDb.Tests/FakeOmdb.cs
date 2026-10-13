using System.Net;
using System.Text;
using Chronicle.Plugin.OMDb;

namespace Chronicle.Plugin.OMDb.Tests;

/// <summary>A stand-in for omdbapi.com that answers from real recorded responses, records every request, and can fail on demand.</summary>
internal sealed class FakeOmdb : HttpMessageHandler
{
    public const string TestKey = "testkey123";

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    private readonly List<(Func<string, bool> Match, Func<string> Body)> _routes = [];

    /// <summary>Every request as the unescaped query, WITHOUT the key ("r=json&amp;i=tt0133093&amp;plot=full"): the tests never see it.</summary>
    public List<string> Queries { get; } = [];
    /// <summary>The raw request URLs, key included, for the one test that proves the key goes where it must and nowhere else.</summary>
    public List<string> RawUrls { get; } = [];
    public int Requests => Queries.Count;

    public HttpStatusCode? FailWith { get; set; }
    public string? FailBody { get; set; }
    public byte[] ImageBytes { get; set; } = [1, 2, 3];
    public List<TimeSpan> Delays { get; } = [];

    public FakeOmdb Route(Func<string, bool> match, string fixture) { _routes.Add((match, () => Fixture(fixture))); return this; }
    public FakeOmdb RouteBody(Func<string, bool> match, string body) { _routes.Add((match, () => body)); return this; }

    public OmdbClient Client(string key = TestKey) =>
        new(new HttpClient(this), key, TimeSpan.Zero, delay: (span, _) => { Delays.Add(span); return Task.CompletedTask; });

    public static FakeOmdb Catalogue() => new FakeOmdb()
        .Route(q => q.Contains("i=tt0133093"), "movie_matrix.json")
        .Route(q => q.Contains("i=tt0903747"), "series_breaking_bad.json")
        .Route(q => q.Contains("i=tt0959621"), "episode_pilot.json")
        .Route(q => q.Contains("i=tt1728310"), "episode_day47_sparse.json")
        .Route(q => q.Contains("i=tt14891408"), "error_getting_data.json")
        .Route(q => q.Contains("i=tt9999999999"), "error_incorrect_id.json")
        .Route(q => q.Contains("s=matrix"), "search_matrix.json")
        .Route(q => q.Contains("t=") && q.Contains("breaking bad", StringComparison.OrdinalIgnoreCase), "title_breaking_bad.json")
        .Route(q => q.Contains("t=zzzzqqqxxnotamovie"), "title_not_found.json");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.RequestUri!.ToString();
        RawUrls.Add(raw);
        var query = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?'));
        query = string.Join('&', query.Split('&').Where(p => !p.StartsWith("apikey=", StringComparison.Ordinal)));
        Queries.Add(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.RequestUri.Host != "www.omdbapi.com")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) });

        if (FailWith is { } status)
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(FailBody ?? "{}", Encoding.UTF8, "application/json") });

        foreach (var (match, body) in _routes)
            if (match(query))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Response\":\"False\",\"Error\":\"Incorrect IMDb ID.\"}", Encoding.UTF8, "application/json"),
        });
    }
}
