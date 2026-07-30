namespace ScienceTimeline.Core;

/// <summary>
/// Точность датировки. Значения совпадают с enum date_precision в БД.
/// </summary>
public enum TimePrecision
{
    Unknown = 0,
    Millennium,
    Century,
    Decade,
    Year,
    Month,
    Day
}

/// <summary>Календарь, в котором дата пришла из источника.</summary>
public enum CalendarModel
{
    Gregorian = 0,
    Julian
}

public static class TimePrecisionExtensions
{
    /// <summary>Строка для enum date_precision в PostgreSQL.</summary>
    public static string ToDbValue(this TimePrecision p) => p switch
    {
        TimePrecision.Day        => "day",
        TimePrecision.Month      => "month",
        TimePrecision.Year       => "year",
        TimePrecision.Decade     => "decade",
        TimePrecision.Century    => "century",
        TimePrecision.Millennium => "millennium",
        _                        => "unknown"
    };

    public static string ToDbValue(this CalendarModel c)
        => c == CalendarModel.Julian ? "julian" : "gregorian";
}
