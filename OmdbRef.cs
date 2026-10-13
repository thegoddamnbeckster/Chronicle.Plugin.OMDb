using System.Text.RegularExpressions;

namespace Chronicle.Plugin.OMDb;

/// <summary>An IMDb title id (tt0133093): the only thing OMDb is asked for by.</summary>
internal sealed record OmdbRef(string ImdbId)
{
    private static readonly Regex Id = new(@"^tt\d{7,10}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InUrl = new(@"imdb\.com/(?:[a-z]{2}/)?title/(tt\d{7,10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Understands: a bare IMDb id (<c>tt0133093</c>), the same with an "imdb:" or "omdb:" prefix (the form the host seeds from other
    /// plugins' ids), and an imdb.com title URL. A person's id (<c>nm…</c>), a bare number and anything else are refused: OMDb knows
    /// titles, and a person's id would only be answered with an error.
    /// </summary>
    public static OmdbRef? Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (InUrl.Match(s) is { Success: true } url) return new OmdbRef(url.Groups[1].Value.ToLowerInvariant());
        foreach (var prefix in new[] { "imdb:", "omdb:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..].Trim(); break; }
        return Id.IsMatch(s) ? new OmdbRef(s.ToLowerInvariant()) : null;
    }
}
