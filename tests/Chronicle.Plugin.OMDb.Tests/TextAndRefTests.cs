using Chronicle.Plugin.OMDb;

namespace Chronicle.Plugin.OMDb.Tests;

public class TextRulesTests
{
    [Theory]
    [InlineData("N/A", null)]
    [InlineData("n/a", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData(" The Matrix ", "The Matrix")]
    public void NotAvailable_IsNothing_NeverText(string? input, string? expected) => Assert.Equal(expected, TextRules.Value(input));

    [Theory]
    [InlineData("136 min", 136)]
    [InlineData("49 min", 49)]
    [InlineData("2 h 15 min", 135)]
    [InlineData("1h 5min", 65)]
    [InlineData("2 h", 120)]
    [InlineData("45", 45)]
    [InlineData("N/A", null)]
    [InlineData("0 min", null)]
    [InlineData("a while", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Runtimes_AreInMinutes(string? text, int? expected) => Assert.Equal(expected, TextRules.RuntimeMinutes(text));

    [Theory]
    [InlineData("1999", 1999)]
    [InlineData("2008–2013", 2008)]
    [InlineData("2020–", 2020)]
    [InlineData("N/A", null)]
    [InlineData("abc", null)]
    [InlineData("0000", null)]
    [InlineData(null, null)]
    public void YearOf_ReadsTheFirstYear(string? text, int? expected) => Assert.Equal(expected, TextRules.YearOf(text));

    [Theory]
    [InlineData("2,284,373", 2284373)]
    [InlineData("53641", 53641)]
    [InlineData("N/A", null)]
    [InlineData("many", null)]
    public void Votes_AreCounts(string text, int? expected) => Assert.Equal(expected, TextRules.Count(text));

    [Theory]
    [InlineData("73/100", 73.0)]
    [InlineData("83%", 83.0)]
    [InlineData("8.7/10", 8.7)]
    [InlineData("8.7", 8.7)]
    [InlineData("N/A", null)]
    public void Scores_AreNumbers(string text, double? expected) => Assert.Equal(expected, TextRules.Score(text));

    [Fact]
    public void ALongList_IsSplitOnCommasOutsideBrackets()
    {
        Assert.Equal(["Bryan Cranston", "Aaron Paul", "Anna Gunn"], TextRules.List("Bryan Cranston, Aaron Paul, Anna Gunn"));
        Assert.Equal(["Vince Gilligan (created by, story)", "Jane Doe"], TextRules.List("Vince Gilligan (created by, story), Jane Doe"));
        Assert.Empty(TextRules.List("N/A"));
        Assert.Empty(TextRules.List(null));
        Assert.Equal(["Single"], TextRules.List(" Single, ,"));
    }

    [Theory]
    [InlineData("Vince Gilligan (created by)", "Vince Gilligan", "created by")]
    [InlineData("Vince Gilligan", "Vince Gilligan", null)]
    [InlineData("Lana Wachowski", "Lana Wachowski", null)]
    [InlineData("(unnamed)", "(unnamed)", null)]
    public void ACreditInBrackets_IsSeparatedFromTheName(string entry, string name, string? credit) =>
        Assert.Equal((name, credit), TextRules.NameAndCredit(entry));

    [Theory]
    [InlineData("Episode #9.54", true)]
    [InlineData("Episode #13.68", true)]
    [InlineData("episode #1", true)]
    [InlineData("Pilot", false)]
    [InlineData("Episode One", false)]
    [InlineData("What I'm Smellin' Is Stinky", false)]
    [InlineData(null, false)]
    public void IMDbsStandInForAnUnnamedEpisode_IsRecognised(string? title, bool expected) => Assert.Equal(expected, TextRules.IsPlaceholderTitle(title));

    [Theory]
    [InlineData("The Matrix", "Matrix")]
    [InlineData("Tom & Jerry", "Tom and Jerry")]
    [InlineData("Léon: The Professional", "Leon The Professional")]
    [InlineData("Ocean's Eleven", "Oceans Eleven")]
    public void Titles_AreComparedWithoutArticlesAccentsOrPunctuation(string a, string b) => Assert.Equal(TextRules.Normalise(a), TextRules.Normalise(b));

    [Theory]
    [InlineData("Ubik (1969)", "Ubik")]
    [InlineData("Ubik", "Ubik")]
    [InlineData("Heat [1995]", "Heat")]
    public void AYearSuffix_IsStripped(string title, string expected) => Assert.Equal(expected, TextRules.StripYearSuffix(title));

    [Theory]
    [InlineData("20 Jan 2008", "2008-01-20")]
    [InlineData("5 Mar 1999", "1999-03-05")]
    [InlineData("N/A", null)]
    [InlineData("soon", null)]
    public void Released_IsAnIsoDate(string text, string? expected) => Assert.Equal(expected, TextRules.IsoDate(text));
}

public class OmdbRefTests
{
    [Theory]
    [InlineData("tt0133093", "tt0133093")]
    [InlineData("TT0133093", "tt0133093")]
    [InlineData("imdb:tt0133093", "tt0133093")]
    [InlineData("omdb:tt0133093", "tt0133093")]
    [InlineData(" imdb: tt0133093 ", "tt0133093")]
    [InlineData("tt27773532", "tt27773532")]
    [InlineData("tt1234567890", "tt1234567890")]
    [InlineData("https://www.imdb.com/title/tt0133093/", "tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0133093/?ref_=nv_sr_1", "tt0133093")]
    [InlineData("https://m.imdb.com/en/title/tt0133093", "tt0133093")]
    public void WhatIsRecognised(string text, string imdb) => Assert.Equal(imdb, OmdbRef.Parse(text)!.ImdbId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nm0000206")]                  // a person: OMDb knows titles
    [InlineData("imdb:nm0000206")]
    [InlineData("0133093")]
    [InlineData("tt12345")]                    // too short to be an IMDb title id
    [InlineData("tt00000000000")]
    [InlineData("tvdb:12345")]
    [InlineData("movie:603")]
    [InlineData("https://www.example.com/title/tt0133093")]
    public void SomethingElse_IsRefused(string? text) => Assert.Null(OmdbRef.Parse(text));
}
