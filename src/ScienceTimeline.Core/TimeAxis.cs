namespace ScienceTimeline.Core;

/// <summary>
/// Ось времени таймлайна: целые сутки, отсчитанные от 1970-01-01.
///
/// Внутри всё считается через номер юлианского дня (JDN) — счётчик суток,
/// не зависящий ни от какого календаря. Благодаря этому григорианская дата
/// и юлианская дата ложатся на одну и ту же ось без отдельного шага
/// «конвертации календарей»: достаточно разобрать каждую своей формулой.
///
/// Годы задаются в астрономической нумерации: 1 г. до н. э. = 0,
/// 2 г. до н. э. = -1 и так далее. Wikidata использует другую нумерацию,
/// перевод живёт в <see cref="WikidataTime"/>.
///
/// Тип long, а не int, выбран сознательно: Wikidata умеет отдавать
/// годы порядка 13,8 млрд (возраст Вселенной), и промежуточное 365*year
/// в формуле JDN должно это переживать.
/// </summary>
public static class TimeAxis
{
    /// <summary>Номер юлианского дня для 1970-01-01 — нуля нашей оси.</summary>
    public const long UnixEpochJdn = 2_440_588L;

    /// <summary>
    /// Деление с округлением вниз. Нужно потому, что в C# оператор /
    /// округляет к нулю, и для отрицательных годов формулы JDN дают
    /// сдвиг на сутки.
    /// </summary>
    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0)))
            q--;
        return q;
    }

    /// <summary>Остаток, согласованный с <see cref="FloorDiv"/> (всегда неотрицательный при b &gt; 0).</summary>
    public static long FloorMod(long a, long b)
    {
        long r = a % b;
        if (r != 0 && ((r < 0) != (b < 0)))
            r += b;
        return r;
    }

    /// <summary>Номер юлианского дня для даты пролептического григорианского календаря.</summary>
    public static long GregorianToJdn(long year, int month, int day)
    {
        long a = FloorDiv(14 - month, 12);
        long y = year + 4800 - a;
        long m = month + 12 * a - 3;

        return day
             + FloorDiv(153 * m + 2, 5)
             + 365 * y
             + FloorDiv(y, 4)
             - FloorDiv(y, 100)
             + FloorDiv(y, 400)
             - 32045;
    }

    /// <summary>Номер юлианского дня для даты юлианского календаря.</summary>
    public static long JulianToJdn(long year, int month, int day)
    {
        long a = FloorDiv(14 - month, 12);
        long y = year + 4800 - a;
        long m = month + 12 * a - 3;

        return day
             + FloorDiv(153 * m + 2, 5)
             + 365 * y
             + FloorDiv(y, 4)
             - 32083;
    }

    /// <summary>Обратное преобразование: номер юлианского дня в григорианскую дату.</summary>
    public static (long Year, int Month, int Day) JdnToGregorian(long jdn)
    {
        long a = jdn + 32044;
        long b = FloorDiv(4 * a + 3, 146097);
        long c = a - FloorDiv(146097 * b, 4);
        long d = FloorDiv(4 * c + 3, 1461);
        long e = c - FloorDiv(1461 * d, 4);
        long m = FloorDiv(5 * e + 2, 153);

        int day = (int)(e - FloorDiv(153 * m + 2, 5) + 1);
        int month = (int)(m + 3 - 12 * FloorDiv(m, 10));
        long year = 100 * b + d - 4800 + FloorDiv(m, 10);

        return (year, month, day);
    }

    /// <summary>Дата пролептического григорианского календаря в номер дня на оси.</summary>
    public static long FromGregorian(long year, int month, int day)
        => GregorianToJdn(year, month, day) - UnixEpochJdn;

    /// <summary>Дата юлианского календаря в номер дня на оси.</summary>
    public static long FromJulian(long year, int month, int day)
        => JulianToJdn(year, month, day) - UnixEpochJdn;

    /// <summary>Номер дня на оси в григорианскую дату.</summary>
    public static (long Year, int Month, int Day) ToGregorian(long dayNumber)
        => JdnToGregorian(dayNumber + UnixEpochJdn);

    /// <summary>Начало года (1 января) в номерах дней.</summary>
    public static long StartOfYear(long year) => FromGregorian(year, 1, 1);

    /// <summary>Начало месяца в номерах дней.</summary>
    public static long StartOfMonth(long year, int month) => FromGregorian(year, month, 1);

    /// <summary>Начало следующего месяца — правая граница полуинтервала месяца.</summary>
    public static long StartOfNextMonth(long year, int month)
        => month == 12 ? FromGregorian(year + 1, 1, 1) : FromGregorian(year, month + 1, 1);
}
