using ScienceTimeline.Core;

namespace ScienceTimeline.Etl;

/// <summary>Событие, собранное из Wikidata и готовое к записи в БД.</summary>
public sealed class EventRecord
{
    public required string WikidataId { get; init; }

    /// <summary>Название и описание по коду языка.</summary>
    public Dictionary<string, Translation> Translations { get; } = [];

    public string Kind { get; set; } = "discovery";

    public long TStart { get; set; }
    public long TEnd { get; set; }
    public TimePrecision Precision { get; set; } = TimePrecision.Unknown;
    public CalendarModel Calendar { get; set; } = CalendarModel.Gregorian;

    /// <summary>Приблизительная ли датировка. Подпись «около …» собирает клиент.</summary>
    public bool Circa { get; set; }

    public int Sitelinks { get; set; }

    public string? ImageUrl { get; set; }
    public string? WikipediaRu { get; set; }
    public string? WikipediaEn { get; set; }

    /// <summary>Ссылка на первоисточник — для публикаций из Crossref это DOI.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Значимость, назначенная источником напрямую. У Crossref нет числа
    /// языковых разделов, поэтому вес считается по журналу и цитированиям.
    /// </summary>
    public float? SignificanceOverride { get; set; }

    /// <summary>Темы, области и классы из Wikidata — сырьё для определения категории и типа.</summary>
    public HashSet<string> Concepts { get; } = [];

    /// <summary>Классы (P31) отдельно: только по ним решается, открытие это или изобретение.</summary>
    public HashSet<string> Types { get; } = [];

    public HashSet<string> PersonIds { get; } = [];
    public HashSet<string> CategorySlugs { get; } = [];

    public bool HasTitle => Translations.Count > 0;

    /// <summary>
    /// Добавляет перевод, не затирая уже имеющийся заголовок пустым описанием.
    /// Название и описание приходят разными строками ответа.
    /// </summary>
    public void AddTranslation(string lang, string? title, string? summary)
    {
        if (!Translations.TryGetValue(lang, out var existing))
            existing = new Translation();

        Translations[lang] = existing with
        {
            Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title,
            Summary = string.IsNullOrWhiteSpace(summary) ? existing.Summary : summary,
        };
    }

    /// <summary>
    /// Значимость в диапазоне [0, 1] — по числу языковых разделов Википедии.
    /// Логарифм, потому что разница между 5 и 15 разделами куда важнее,
    /// чем между 200 и 210. Именно по этому числу отбирается топ-K событий
    /// в бакете, когда лента отдалена и все точки не помещаются.
    /// </summary>
    public float Significance
    {
        get
        {
            if (SignificanceOverride is { } assigned) return assigned;
            if (Sitelinks <= 0) return 0f;

            double v = Math.Log(1 + Sitelinks) / Math.Log(1 + 400);
            return (float)Math.Clamp(v, 0d, 1d);
        }
    }

    /// <summary>
    /// Событие точнее другого, если у него мельче единица датировки,
    /// а при равной точности — если оно раньше. Нужно, когда у одного
    /// элемента Wikidata несколько утверждений о дате открытия.
    /// </summary>
    public bool IsBetterDateThan(EventRecord other)
        => Precision != other.Precision
            ? Precision > other.Precision
            : TStart < other.TStart;
}

/// <summary>Название и описание события на одном языке.</summary>
public readonly record struct Translation(string? Title = null, string? Summary = null)
{
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>Учёный.</summary>
public sealed class PersonRecord
{
    public required string WikidataId { get; init; }

    /// <summary>Имя по коду языка.</summary>
    public Dictionary<string, string> Names { get; } = [];

    public string? ImageUrl { get; set; }

    public bool HasName => Names.Count > 0;

    /// <summary>Имя на запрошенном языке, иначе английское, иначе любое.</summary>
    public string? Name(string lang)
        => Names.TryGetValue(lang, out var name) ? name
         : Names.TryGetValue("en", out var english) ? english
         : Names.Values.FirstOrDefault();
}
