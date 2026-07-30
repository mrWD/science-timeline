namespace ScienceTimeline.Core;

/// <summary>
/// Человекочитаемые подписи дат — «30 июля 2026 года», «около 300 года до н. э.»,
/// «XX век». Вынесено отдельно от разбора, потому что нужно и импорту
/// (записать в БД), и API (собрать подпись для карточки на лету).
///
/// Годы здесь — астрономические: 1 г. до н. э. = 0, 2 г. до н. э. = -1.
///
/// Признак «около» обрабатывается прямо здесь, а не приклеиванием префикса
/// снаружи: по-русски «около» требует родительного падежа, и «около 300 год»
/// вместо «около 300 года» сразу бросается в глаза.
/// </summary>
public static class TimeLabel
{
    private static readonly string[] MonthsRuNominative =
    [
        "январь", "февраль", "март", "апрель", "май", "июнь",
        "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь"
    ];

    private static readonly string[] MonthsRuGenitive =
    [
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря"
    ];

    private static readonly string[] MonthsEn =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    /// <summary>Год до нашей эры для астрономического года, или null для годов нашей эры.</summary>
    public static long? BcYear(long astronomicalYear)
        => astronomicalYear <= 0 ? 1 - astronomicalYear : null;

    /// <summary>
    /// Границы столетия или тысячелетия, содержащего указанный год.
    ///
    /// Считается по историческому счёту, где нулевого года не существует:
    /// XX век — это 1901–2000, а I век до н. э. — 100–1 гг. до н. э.
    /// Наивное округление вниз до сотни дало бы подпись «XX век» для 1900 года,
    /// что неверно.
    /// </summary>
    /// <returns>Полуинтервал астрономических годов [start, end) и номер единицы.</returns>
    public static (long StartYear, long EndYear, long Index, bool IsBc) UnitRange(long astronomicalYear, long unitSize)
    {
        // Линейный исторический номер года: пропускает ноль.
        long l = astronomicalYear > 0 ? astronomicalYear : astronomicalYear - 1;

        bool bc = l < 0;
        long magnitude = bc ? -l : l;
        long index = TimeAxis.FloorDiv(magnitude - 1, unitSize) + 1;

        long startL, endL;
        if (!bc)
        {
            startL = (index - 1) * unitSize + 1;
            endL   = index * unitSize + 1;
        }
        else
        {
            startL = -(index * unitSize);
            endL   = index == 1 ? 1 : -((index - 1) * unitSize);
        }

        return (ToAstronomical(startL), ToAstronomical(endL), index, bc);

        static long ToAstronomical(long l) => l > 0 ? l : l + 1;
    }

    /// <summary>Римская запись числа. Нужна для подписей веков и тысячелетий.</summary>
    public static string Roman(long n)
    {
        if (n <= 0) return n.ToString();

        (int Value, string Sign)[] table =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];

        var sb = new System.Text.StringBuilder();
        foreach (var (value, sign) in table)
            while (n >= value) { sb.Append(sign); n -= value; }

        return sb.ToString();
    }

    /// <summary>Английский порядковый суффикс: 1st, 2nd, 3rd, 4th…</summary>
    public static string Ordinal(long n)
    {
        long lastTwo = n % 100;
        if (lastTwo is >= 11 and <= 13) return $"{n}th";
        return (n % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th"
        };
    }

    /// <summary>Русская подпись даты.</summary>
    public static string Ru(long year, int month, int day, TimePrecision precision, bool circa = false)
    {
        long? bc = BcYear(year);
        string era = bc is null ? "" : " до н. э.";
        long y = bc ?? year;
        string about = circa ? "около " : "";

        switch (precision)
        {
            case TimePrecision.Day:
                // «30 июня 1905 года» — родительный падеж и без «около».
                return $"{about}{day} {MonthsRuGenitive[month - 1]} {y} года{era}";

            case TimePrecision.Month:
                return circa
                    ? $"около {MonthsRuGenitive[month - 1]} {y} года{era}"
                    : $"{MonthsRuNominative[month - 1]} {y} года{era}";

            case TimePrecision.Year:
                return circa
                    ? $"около {y} года{era}"
                    : $"{y} год{era}";

            case TimePrecision.Decade:
            {
                long decadeStart = TimeAxis.FloorDiv(year, 10) * 10;
                long? decadeBc = BcYear(decadeStart);
                long d = decadeBc ?? decadeStart;
                string decadeEra = decadeBc is null ? "" : " до н. э.";

                return circa
                    ? $"около {d}-х годов{decadeEra}"
                    : $"{d}-е годы{decadeEra}";
            }

            case TimePrecision.Century:
            {
                var (_, _, index, isBc) = UnitRange(year, 100);
                string centuryEra = isBc ? " до н. э." : "";

                return circa
                    ? $"около {Roman(index)} века{centuryEra}"
                    : $"{Roman(index)} век{centuryEra}";
            }

            case TimePrecision.Millennium:
            {
                var (_, _, index, isBc) = UnitRange(year, 1000);
                string millenniumEra = isBc ? " до н. э." : "";

                return circa
                    ? $"около {Roman(index)} тысячелетия{millenniumEra}"
                    : $"{Roman(index)} тысячелетие{millenniumEra}";
            }

            default:
                return "дата неизвестна";
        }
    }

    /// <summary>Английская подпись даты.</summary>
    public static string En(long year, int month, int day, TimePrecision precision, bool circa = false)
    {
        long? bc = BcYear(year);
        string era = bc is null ? "" : " BC";
        long y = bc ?? year;
        string about = circa ? "c. " : "";

        switch (precision)
        {
            case TimePrecision.Day:
                return $"{about}{day} {MonthsEn[month - 1]} {y}{era}";

            case TimePrecision.Month:
                return $"{about}{MonthsEn[month - 1]} {y}{era}";

            case TimePrecision.Year:
                return $"{about}{y}{era}";

            case TimePrecision.Decade:
            {
                long decadeStart = TimeAxis.FloorDiv(year, 10) * 10;
                long? decadeBc = BcYear(decadeStart);

                return decadeBc is null
                    ? $"{about}{decadeStart}s"
                    : $"{about}{decadeBc}s BC";
            }

            case TimePrecision.Century:
            {
                var (_, _, index, isBc) = UnitRange(year, 100);
                return isBc ? $"{about}{Ordinal(index)} century BC" : $"{about}{Ordinal(index)} century";
            }

            case TimePrecision.Millennium:
            {
                var (_, _, index, isBc) = UnitRange(year, 1000);
                return isBc ? $"{about}{Ordinal(index)} millennium BC" : $"{about}{Ordinal(index)} millennium";
            }

            default:
                return "date unknown";
        }
    }
}
