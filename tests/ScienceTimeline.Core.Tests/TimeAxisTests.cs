using ScienceTimeline.Core;

namespace ScienceTimeline.Core.Tests;

public class TimeAxisTests
{
    [Fact]
    public void Epoch_IsZero()
    {
        Assert.Equal(0, TimeAxis.FromGregorian(1970, 1, 1));
    }

    // Эталонные значения взяты из самого PostgreSQL (select tl_day(...)),
    // чтобы ось в C# и ось в БД гарантированно совпадали. Если этот тест
    // упадёт, значит импорт и запросы разъехались между собой.
    [Theory]
    [InlineData(1970, 1, 1, 0L)]
    [InlineData(1905, 6, 30, -23561L)]
    [InlineData(-299, 1, 1, -828735L)]      // 300 г. до н. э. в астрономической нумерации
    [InlineData(-4712, 1, 1, -2440550L)]    // 4713 г. до н. э. — нижняя граница дат PostgreSQL
    public void MatchesPostgresReferenceValues(int year, int month, int day, long expected)
    {
        Assert.Equal(expected, TimeAxis.FromGregorian(year, month, day));
    }

    [Theory]
    [InlineData(2026, 7, 30)]
    [InlineData(1969, 12, 31)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 12, 31)]      // 1 г. до н. э.
    [InlineData(-43, 3, 15)]     // 44 г. до н. э.
    [InlineData(-2999, 6, 15)]
    [InlineData(1600, 2, 29)]    // високосный по григорианскому
    [InlineData(2000, 2, 29)]
    public void GregorianRoundTrips(int year, int month, int day)
    {
        long axis = TimeAxis.FromGregorian(year, month, day);
        var back = TimeAxis.ToGregorian(axis);

        Assert.Equal(((long)year, month, day), back);
    }

    [Fact]
    public void ConsecutiveDaysDifferByOne()
    {
        // Переход через нулевой год — место, где truncating-деление дало бы сбой.
        long lastDayOf1Bc = TimeAxis.FromGregorian(0, 12, 31);
        long firstDayOf1Ad = TimeAxis.FromGregorian(1, 1, 1);

        Assert.Equal(1, firstDayOf1Ad - lastDayOf1Bc);
    }

    // Григорианская реформа: день после 4 октября 1582 по юлианскому календарю —
    // это 15 октября по григорианскому. Значит юлианское 4 октября и
    // григорианское 14 октября — одни и те же сутки.
    [Fact]
    public void JulianAndGregorianAgreeAtGregorianReform()
    {
        Assert.Equal(
            TimeAxis.FromGregorian(1582, 10, 14),
            TimeAxis.FromJulian(1582, 10, 4));
    }

    // Ньютон родился 25 декабря 1642 по юлианскому календарю,
    // что соответствует 4 января 1643 по григорианскому.
    [Fact]
    public void NewtonsBirthdayConvertsBetweenCalendars()
    {
        Assert.Equal(
            TimeAxis.FromGregorian(1643, 1, 4),
            TimeAxis.FromJulian(1642, 12, 25));
    }

    // С 1 марта 200 года по 28 февраля 300 года расхождение юлианского и
    // григорианского календарей равно нулю — это единственное окно, где обе
    // формулы обязаны дать один и тот же ответ на одну и ту же запись даты.
    [Fact]
    public void CalendarsCoincideBetweenMarch200AndFebruary300()
    {
        Assert.Equal(TimeAxis.FromGregorian(200, 3, 1),   TimeAxis.FromJulian(200, 3, 1));
        Assert.Equal(TimeAxis.FromGregorian(250, 7, 15),  TimeAxis.FromJulian(250, 7, 15));
        Assert.Equal(TimeAxis.FromGregorian(300, 2, 28),  TimeAxis.FromJulian(300, 2, 28));
    }

    // 200 год високосный по юлианскому календарю, но не по григорианскому:
    // он делится на 100 и не делится на 400. Поэтому юлианское 29 февраля
    // приходится на григорианское 28 февраля — дня 29 февраля там просто нет.
    [Fact]
    public void JulianLeapDayOfYear200MapsToFebruary28()
    {
        Assert.Equal(
            TimeAxis.FromGregorian(200, 2, 28),
            TimeAxis.FromJulian(200, 2, 29));
    }

    [Theory]
    [InlineData(7, 2, 3)]
    [InlineData(-7, 2, -4)]
    [InlineData(-1, 10, -1)]
    [InlineData(0, 10, 0)]
    public void FloorDivRoundsTowardsNegativeInfinity(long a, long b, long expected)
    {
        Assert.Equal(expected, TimeAxis.FloorDiv(a, b));
    }
}
