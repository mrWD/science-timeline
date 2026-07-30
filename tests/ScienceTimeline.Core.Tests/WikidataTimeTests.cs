using ScienceTimeline.Core;

namespace ScienceTimeline.Core.Tests;

public class WikidataTimeTests
{
    private static ParsedTime Parse(
        string value,
        int precision,
        string? calendar = WikidataTime.ProlepticGregorianUri,
        bool circa = false)
    {
        Assert.True(
            WikidataTime.TryParse(value, precision, calendar, circa, out var parsed),
            $"не разобралось: {value} (точность {precision})");
        return parsed;
    }

    [Fact]
    public void ParsesExactDay()
    {
        var t = Parse("+1905-06-30T00:00:00Z", 11);

        Assert.Equal(TimePrecision.Day, t.Precision);
        Assert.Equal(TimeAxis.FromGregorian(1905, 6, 30), t.Start);
        Assert.Equal(1, t.End - t.Start);
        Assert.Equal("30 июня 1905 года", t.DisplayRu);
        Assert.Equal("30 June 1905", t.DisplayEn);
    }

    // Ловушка 3: при грубой точности Wikidata пишет нулевые месяц и день.
    [Fact]
    public void ParsesYearWithZeroMonthAndDay()
    {
        var t = Parse("+1905-00-00T00:00:00Z", 9);

        Assert.Equal(TimePrecision.Year, t.Precision);
        Assert.Equal(TimeAxis.FromGregorian(1905, 1, 1), t.Start);
        Assert.Equal(TimeAxis.FromGregorian(1906, 1, 1), t.End);
        Assert.Equal("1905 год", t.DisplayRu);
    }

    // Ловушка 1: «-0044» у Wikidata — это 44 г. до н. э., то есть
    // астрономический год -43, а не -44.
    [Fact]
    public void ShiftsBcYearsToAstronomicalNumbering()
    {
        var t = Parse("-0044-03-15T00:00:00Z", 11, WikidataTime.ProlepticJulianUri);

        Assert.Equal(TimeAxis.FromJulian(-43, 3, 15), t.Start);
        Assert.Equal("15 марта 44 года до н. э.", t.DisplayRu);
        Assert.Equal("15 March 44 BC", t.DisplayEn);
    }

    // Ловушка 2: одна и та же дата в разных календарях должна давать
    // разные точки на оси, иначе античность и средневековье уедут.
    [Fact]
    public void JulianCalendarShiftsResultRelativeToGregorian()
    {
        var julian    = Parse("+1582-10-04T00:00:00Z", 11, WikidataTime.ProlepticJulianUri);
        var gregorian = Parse("+1582-10-04T00:00:00Z", 11, WikidataTime.ProlepticGregorianUri);

        Assert.Equal(10, julian.Start - gregorian.Start);
        Assert.Equal(CalendarModel.Julian, julian.Calendar);
    }

    [Fact]
    public void NullCalendarModelIsTreatedAsGregorian()
    {
        var t = Parse("+1905-06-30T00:00:00Z", 11, calendar: null);
        Assert.Equal(CalendarModel.Gregorian, t.Calendar);
    }

    [Fact]
    public void ParsesMonthPrecision()
    {
        var t = Parse("+1905-11-00T00:00:00Z", 10);

        Assert.Equal(TimePrecision.Month, t.Precision);
        Assert.Equal(TimeAxis.FromGregorian(1905, 11, 1), t.Start);
        Assert.Equal(TimeAxis.FromGregorian(1905, 12, 1), t.End);
        Assert.Equal("ноябрь 1905 года", t.DisplayRu);
        Assert.Equal("November 1905", t.DisplayEn);
    }

    [Fact]
    public void DecemberMonthPrecisionRollsOverToNextYear()
    {
        var t = Parse("+1905-12-00T00:00:00Z", 10);
        Assert.Equal(TimeAxis.FromGregorian(1906, 1, 1), t.End);
    }

    [Fact]
    public void ParsesDecade()
    {
        var t = Parse("+1927-00-00T00:00:00Z", 8);

        Assert.Equal(TimePrecision.Decade, t.Precision);
        Assert.Equal(TimeAxis.FromGregorian(1920, 1, 1), t.Start);
        Assert.Equal(TimeAxis.FromGregorian(1930, 1, 1), t.End);
        Assert.Equal("1920-е годы", t.DisplayRu);
        Assert.Equal("1920s", t.DisplayEn);
    }

    // Век считается по историческому счёту: XX век — это 1901–2000.
    [Fact]
    public void CenturySpansHistoricalBoundaries()
    {
        var t = Parse("+1901-00-00T00:00:00Z", 7);

        Assert.Equal(TimePrecision.Century, t.Precision);
        Assert.Equal(TimeAxis.FromGregorian(1901, 1, 1), t.Start);
        Assert.Equal(TimeAxis.FromGregorian(2001, 1, 1), t.End);
        Assert.Equal("XX век", t.DisplayRu);
        Assert.Equal("20th century", t.DisplayEn);
    }

    [Fact]
    public void BcCenturyEndsAtFirstYearOfCommonEra()
    {
        // III век до н. э. — это 300–201 гг. до н. э.
        var t = Parse("-0300-00-00T00:00:00Z", 7);

        Assert.Equal("III век до н. э.", t.DisplayRu);
        Assert.Equal("3rd century BC", t.DisplayEn);
        Assert.Equal(TimeAxis.FromGregorian(-299, 1, 1), t.Start);
        Assert.Equal(TimeAxis.FromGregorian(-199, 1, 1), t.End);
    }

    [Fact]
    public void FirstCenturyBcEndsAtYearOne()
    {
        var t = Parse("-0044-00-00T00:00:00Z", 7);

        Assert.Equal("I век до н. э.", t.DisplayRu);
        Assert.Equal(TimeAxis.FromGregorian(1, 1, 1), t.End);
    }

    [Fact]
    public void ParsesMillennium()
    {
        var t = Parse("-2500-00-00T00:00:00Z", 6);

        Assert.Equal(TimePrecision.Millennium, t.Precision);
        Assert.Equal("III тысячелетие до н. э.", t.DisplayRu);
        Assert.Equal("3rd millennium BC", t.DisplayEn);
    }

    [Fact]
    public void CircaQualifierIsReflectedInLabel()
    {
        var t = Parse("-0300-00-00T00:00:00Z", 9, circa: true);

        Assert.Equal("около 300 года до н. э.", t.DisplayRu);
        Assert.Equal("c. 300 BC", t.DisplayEn);
    }

    [Fact]
    public void CircaUsesGenitiveForCoarserUnits()
    {
        Assert.Equal("около III века до н. э.",        Parse("-0300-00-00T00:00:00Z", 7, circa: true).DisplayRu);
        Assert.Equal("около III тысячелетия до н. э.", Parse("-2500-00-00T00:00:00Z", 6, circa: true).DisplayRu);
        Assert.Equal("около 1920-х годов",             Parse("+1927-00-00T00:00:00Z", 8, circa: true).DisplayRu);
    }

    // Точность грубее тысячелетия — это геология и космология,
    // на ленте истории науки им делать нечего.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void RejectsPrecisionCoarserThanMillennium(int precision)
    {
        Assert.False(WikidataTime.TryParse("-13798000000-00-00T00:00:00Z", precision, null, false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не дата")]
    [InlineData("+1905-13-01T00:00:00Z")]   // тринадцатый месяц
    public void RejectsMalformedInput(string? value)
    {
        Assert.False(WikidataTime.TryParse(value, 11, null, false, out _));
    }

    [Fact]
    public void IntervalIsAlwaysNonEmpty()
    {
        foreach (var (value, precision) in new[]
                 {
                     ("+2026-07-30T00:00:00Z", 11),
                     ("+2026-07-00T00:00:00Z", 10),
                     ("+2026-00-00T00:00:00Z", 9),
                     ("-0001-00-00T00:00:00Z", 9),
                     ("-0300-00-00T00:00:00Z", 7),
                     ("-2500-00-00T00:00:00Z", 6),
                 })
        {
            var t = Parse(value, precision);
            Assert.True(t.End > t.Start, $"пустой интервал для {value}/{precision}");
        }
    }
}
