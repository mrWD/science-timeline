using System.Globalization;
using System.Text.RegularExpressions;

namespace ScienceTimeline.Core;

/// <summary>Дата, разобранная в интервал на оси таймлайна.</summary>
public readonly record struct ParsedTime(
    long Start,
    long End,
    TimePrecision Precision,
    CalendarModel Calendar,
    string DisplayRu,
    string DisplayEn);

/// <summary>
/// Разбор дат Wikidata. Самое хрупкое место импорта, поэтому вынесено
/// отдельно и покрыто тестами.
///
/// Ловушек здесь три, и каждая молча портит данные:
///
/// 1. Нумерация годов. Wikidata пишет 44 г. до н. э. как «-0044», то есть
///    года 0 в её счёте нет. В астрономической нумерации, на которой стоит
///    наша ось, 44 г. до н. э. — это год -43. Разница в единицу, но она
///    сдвигает всю античность.
///
/// 2. Календарь. До 1582 года Wikidata обычно отдаёт юлианскую дату
///    с моделью Q1985786. Если разобрать её григорианской формулой,
///    событие уедет на 10–13 суток. Мы считаем номер юлианского дня
///    формулой того календаря, который указан, — тогда обе даты
///    оказываются на одной оси без отдельной конвертации.
///
/// 3. Нулевые месяц и день. При грубой точности Wikidata пишет
///    «+1905-00-00T00:00:00Z». Наивный парсер на этом падает
///    или получает нулевой месяц.
/// </summary>
public static partial class WikidataTime
{
    public const string ProlepticGregorianUri = "http://www.wikidata.org/entity/Q1985727";
    public const string ProlepticJulianUri    = "http://www.wikidata.org/entity/Q1985786";

    [GeneratedRegex(@"^([+-]?)(\d+)-(\d{1,2})-(\d{1,2})T", RegexOptions.CultureInvariant)]
    private static partial Regex TimeLiteral();

    /// <summary>
    /// Разбирает значение времени Wikidata в интервал на оси.
    /// </summary>
    /// <param name="timeValue">Литерал вида «+1905-06-30T00:00:00Z».</param>
    /// <param name="wikidataPrecision">Код точности Wikidata: 11 — день, 10 — месяц, 9 — год, 8 — десятилетие, 7 — век, 6 — тысячелетие.</param>
    /// <param name="calendarModelUri">URI модели календаря; null трактуется как григорианский.</param>
    /// <param name="isCirca">Стоит ли у утверждения квалификатор «около» (P1480 = Q5727902).</param>
    public static bool TryParse(
        string? timeValue,
        int wikidataPrecision,
        string? calendarModelUri,
        bool isCirca,
        out ParsedTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(timeValue)) return false;

        var match = TimeLiteral().Match(timeValue);
        if (!match.Success) return false;

        bool negative = match.Groups[1].Value == "-";
        if (!long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long rawYear))
            return false;   // год длиннее, чем помещается в long — для нас это не наука, а космология

        int month = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int day   = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

        // Ловушка 3: грубая точность приходит с нулями.
        if (month == 0) month = 1;
        if (day == 0) day = 1;
        if (month is < 1 or > 12) return false;
        if (day is < 1 or > 31) return false;

        // Ловушка 1: перевод в астрономическую нумерацию.
        long year = negative ? -rawYear + 1 : rawYear;

        // Ловушка 2: считаем формулой того календаря, который указан.
        var calendar = calendarModelUri == ProlepticJulianUri ? CalendarModel.Julian : CalendarModel.Gregorian;
        long ToAxis(long y, int m, int d) => calendar == CalendarModel.Julian
            ? TimeAxis.FromJulian(y, m, d)
            : TimeAxis.FromGregorian(y, m, d);

        long start, end;
        TimePrecision precision;

        switch (wikidataPrecision)
        {
            // 12–14 — часы, минуты, секунды. Для таймлайна это тот же день.
            case >= 11:
                precision = TimePrecision.Day;
                start = ToAxis(year, month, day);
                end   = start + 1;
                break;

            case 10:
                precision = TimePrecision.Month;
                start = ToAxis(year, month, 1);
                end   = month == 12 ? ToAxis(year + 1, 1, 1) : ToAxis(year, month + 1, 1);
                break;

            case 9:
                precision = TimePrecision.Year;
                start = ToAxis(year, 1, 1);
                end   = ToAxis(year + 1, 1, 1);
                break;

            case 8:
            {
                precision = TimePrecision.Decade;
                long decadeStart = TimeAxis.FloorDiv(year, 10) * 10;
                start = ToAxis(decadeStart, 1, 1);
                end   = ToAxis(decadeStart + 10, 1, 1);
                break;
            }

            case 7:
            {
                precision = TimePrecision.Century;
                var (s, e, _, _) = TimeLabel.UnitRange(year, 100);
                start = ToAxis(s, 1, 1);
                end   = ToAxis(e, 1, 1);
                break;
            }

            case 6:
            {
                precision = TimePrecision.Millennium;
                var (s, e, _, _) = TimeLabel.UnitRange(year, 1000);
                start = ToAxis(s, 1, 1);
                end   = ToAxis(e, 1, 1);
                break;
            }

            // 0–5 — от десяти тысяч до миллиарда лет. Это геология и космология,
            // а не история науки; на ленту такие события не кладём.
            default:
                return false;
        }

        if (end <= start) return false;

        string ru = TimeLabel.Ru(year, month, day, precision, isCirca);
        string en = TimeLabel.En(year, month, day, precision, isCirca);

        result = new ParsedTime(start, end, precision, calendar, ru, en);
        return true;
    }
}
