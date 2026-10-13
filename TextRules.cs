using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.OMDb;

/// <summary>
/// Turns OMDb's text answers into values: every field is a string, "N/A" where there is nothing, runtimes are "2 h 15 min", votes
/// are "2,284,373", writers carry their credit in brackets. Pure functions, no network.
/// </summary>
internal static partial class TextRules
{
    /// <summary>The text, or null for OMDb's "N/A" and for blanks.</summary>
    public static string? Value(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();

    // ── numbers ───────────────────────────────────────────────────────────────

    [GeneratedRegex(@"(?:(\d+)\s*h(?:ours?|rs?)?)?\s*(?:(\d+)\s*m(?:in(?:ute)?s?)?)?", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimePattern();

    /// <summary>"136 min" is 136, "2 h 15 min" is 135, "2h" is 120, a bare "45" is 45; anything else null.</summary>
    public static int? RuntimeMinutes(string? text)
    {
        var s = Value(text);
        if (s is null) return null;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var bare)) return bare > 0 ? bare : null;
        var m = RuntimePattern().Match(s);
        if (!m.Success || m.Length != s.Length) return null;
        var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var total = hours * 60 + minutes;
        return total > 0 ? total : null;
    }

    /// <summary>The first year in text like "1999", "2008–2013" or "2020–".</summary>
    public static int? YearOf(string? text)
    {
        var s = Value(text);
        return s is { Length: >= 4 } && int.TryParse(s.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1800 and < 2200 ? y : null;
    }

    public static double? Decimal(string? text) =>
        Value(text) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>"2,284,373" is 2284373.</summary>
    public static int? Count(string? text) =>
        Value(text) is { } s && int.TryParse(s.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"73/100" is 73; "83%" is 83; "8.7/10" is 8.7.</summary>
    public static double? Score(string? text)
    {
        var s = Value(text);
        if (s is null) return null;
        var cut = s.IndexOfAny(['/', '%']);
        return Decimal(cut > 0 ? s[..cut] : s);
    }

    // ── lists ─────────────────────────────────────────────────────────────────

    /// <summary>Splits "A, B, C" on commas that are not inside brackets: "Vince Gilligan (created by, story), Jane Doe" is two names.</summary>
    public static List<string> List(string? text)
    {
        var s = Value(text);
        var result = new List<string>();
        if (s is null) return result;
        var depth = 0;
        var current = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')' && depth > 0) depth--;
            if (ch == ',' && depth == 0) { Add(); continue; }
            current.Append(ch);
        }
        Add();
        return result;

        void Add()
        {
            var item = current.ToString().Trim();
            current.Clear();
            if (item.Length > 0 && !item.Equals("N/A", StringComparison.OrdinalIgnoreCase)) result.Add(item);
        }
    }

    [GeneratedRegex(@"^(?<name>.*?)\s*\((?<role>[^()]*)\)\s*$")]
    private static partial Regex Credited();

    /// <summary>"Vince Gilligan (created by)" is ("Vince Gilligan", "created by"); a name with no brackets has no credit.</summary>
    public static (string Name, string? Credit) NameAndCredit(string entry)
    {
        var m = Credited().Match(entry);
        return m.Success && m.Groups["name"].Value.Length > 0 ? (m.Groups["name"].Value.Trim(), m.Groups["role"].Value.Trim()) : (entry.Trim(), null);
    }

    // ── titles ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^Episode\s*#\s*\d+(\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderEpisode();

    /// <summary>IMDb's own stand-in for an episode nobody has named: "Episode #9.54".</summary>
    public static bool IsPlaceholderTitle(string? title) => title is not null && PlaceholderEpisode().IsMatch(title.Trim());

    /// <summary>Folds accents, lower-cases, keeps letters and digits ("&amp;" reads as "and"), drops a leading article.</summary>
    public static string Normalise(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var decomposed = title.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ') sb.Append(' ');
            else if (ch is not ('\'' or '’' or '.')) sb.Append(' ');
        }
        var folded = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        foreach (var article in new[] { "the ", "a ", "an " })
            if (folded.StartsWith(article, StringComparison.Ordinal) && folded.Length > article.Length)
            { folded = folded[article.Length..]; break; }
        return folded.Replace(" ", "");
    }

    [GeneratedRegex(@"\s*[\(\[](1\d{3}|20\d{2})[\)\]]\s*$")]
    private static partial Regex YearSuffix();

    public static string StripYearSuffix(string? title) => YearSuffix().Replace((title ?? "").Trim(), "").Trim();

    // ── dates ─────────────────────────────────────────────────────────────────

    /// <summary>"20 Jan 2008" as "2008-01-20"; null when it is not a date.</summary>
    public static string? IsoDate(string? text) =>
        Value(text) is { } s && DateTime.TryParseExact(s, ["d MMM yyyy", "dd MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
