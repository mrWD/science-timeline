using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ScienceTimeline.Core;

namespace ScienceTimeline.Etl;

/// <summary>
/// Импорт свежих публикаций из Crossref.
///
/// Wikidata историю науки описывает хорошо, а текущую — почти никак:
/// на весь 2025 год там нашлось двенадцать событий, на 2026-й — ни одного.
/// Открытие попадает в Wikidata спустя месяцы и годы после публикации,
/// поэтому лента без второго источника обрывается за годы до сегодняшнего дня.
///
/// Crossref знает все статьи с DOI, но их миллионы в год, и брать всё подряд
/// бессмысленно: получится не история науки, а список литературы. Поэтому
/// отбор идёт по ISSN двух десятков ведущих журналов. Это грубый фильтр,
/// зато честный и дешёвый: у «важности только что вышедшей статьи» бесплатного
/// признака не существует — цитирования появляются через годы.
/// </summary>
public sealed partial class CrossrefSource : IDisposable
{
    private const string Endpoint = "https://api.crossref.org/works";

    /// <summary>
    /// Журналы, попадающие в ленту, и область науки по умолчанию.
    /// У многопрофильных изданий области нет — она берётся из рубрик статьи.
    /// </summary>
    private static readonly (string Issn, string Journal, string? Category)[] Journals =
    [
        ("0028-0836", "Nature",                          null),
        ("1476-4687", "Nature",                          null),
        ("0036-8075", "Science",                         null),
        ("1095-9203", "Science",                         null),
        ("0027-8424", "PNAS",                            null),
        ("1091-6490", "PNAS",                            null),
        ("0092-8674", "Cell",                            "biology"),
        ("0140-6736", "The Lancet",                      "medicine"),
        ("0028-4793", "New England Journal of Medicine", "medicine"),
        ("0098-7484", "JAMA",                            "medicine"),
        ("1078-8956", "Nature Medicine",                 "medicine"),
        ("0031-9007", "Physical Review Letters",         "physics"),
        ("1079-7114", "Physical Review Letters",         "physics"),
        ("1745-2473", "Nature Physics",                  "physics"),
        ("1755-4330", "Nature Chemistry",                "chemistry"),
        ("2397-3366", "Nature Astronomy",                "astronomy"),
        ("1087-0156", "Nature Biotechnology",            "biology"),
        ("1476-1122", "Nature Materials",                "chemistry"),
        ("1758-678X", "Nature Climate Change",           "earth"),
        ("1752-0894", "Nature Geoscience",               "earth"),
    ];

    /// <summary>Рубрики Crossref, сводимые к нашим областям науки.</summary>
    private static readonly (string Needle, string Category)[] SubjectMap =
    [
        ("physic", "physics"),
        ("astron", "astronomy"),
        ("space", "astronomy"),
        ("chemi", "chemistry"),
        ("materials", "chemistry"),
        ("biolog", "biology"),
        ("genetic", "biology"),
        ("biochem", "biology"),
        ("cell", "biology"),
        ("ecolog", "biology"),
        ("evolution", "biology"),
        ("medic", "medicine"),
        ("health", "medicine"),
        ("immuno", "medicine"),
        ("neuro", "medicine"),
        ("cancer", "medicine"),
        ("earth", "earth"),
        ("geo", "earth"),
        ("climate", "earth"),
        ("environment", "earth"),
        ("atmospher", "earth"),
        ("comput", "computing"),
        ("artificial intelligence", "computing"),
        ("mathemat", "mathematics"),
        ("statistic", "mathematics"),
        ("engineer", "engineering"),
        ("psycholog", "psychology"),
        ("social", "social"),
        ("econom", "social"),
    ];

    private readonly HttpClient _http;

    public CrossrefSource()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // Crossref пускает в «вежливый пул» — очередь с более щедрыми лимитами —
        // тех, кто указал контакт в User-Agent.
        _http.DefaultRequestHeaders.Add(
            "User-Agent",
            "science-timeline/0.1 (https://github.com/mrWD/science-timeline; mailto:lvigtor@gmail.com)");
    }

    public async Task<List<EventRecord>> FetchAsync(DateOnly since, int maxItems, CancellationToken ct)
    {
        var events = new List<EventRecord>();
        string cursor = "*";
        int page = 0;

        while (events.Count < maxItems)
        {
            var filters = new List<string> { "type:journal-article", $"from-pub-date:{since:yyyy-MM-dd}" };
            filters.AddRange(Journals.Select(j => $"issn:{j.Issn}"));

            // Сортировка по дате убыванием обязательна. Эти журналы дают около
            // двух тысяч статей в месяц, и предел выборки почти всегда срабатывает;
            // без сортировки он обрезал бы выдачу в произвольном месте и выкинул
            // ровно то, ради чего всё затевалось, — последние недели.
            string url = $"{Endpoint}?filter={string.Join(",", filters)}"
                       + $"&rows=1000&cursor={Uri.EscapeDataString(cursor)}"
                       + "&sort=published&order=desc"
                       + "&select=DOI,title,published,issued,container-title,is-referenced-by-count,subject,author";

            var message = await GetAsync(url, ct);
            if (message is null) break;

            foreach (var item in message.Items)
            {
                var record = Convert(item);
                if (record is not null) events.Add(record);
            }

            page++;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   страница {page}: всего {events.Count} публикаций");

            if (message.Items.Count == 0 || string.IsNullOrEmpty(message.NextCursor)) break;
            cursor = message.NextCursor;
        }

        return events;
    }

    private async Task<CrossrefMessage?> GetAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"  Crossref вернул {(int)response.StatusCode}");
                    if (attempt == 4) return null;
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var parsed = await JsonSerializer.DeserializeAsync<CrossrefResponse>(stream, cancellationToken: ct);
                return parsed?.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                if (attempt == 4)
                {
                    Console.Error.WriteLine($"  Crossref недоступен: {ex.Message}");
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }

        return null;
    }

    private static EventRecord? Convert(CrossrefItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Doi)) return null;

        string? title = item.Title?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(title)) return null;

        var parts = item.Published?.DateParts?.FirstOrDefault() ?? item.Issued?.DateParts?.FirstOrDefault();
        if (parts is null || parts.Count == 0 || parts[0] is not { } year) return null;

        // Crossref отдаёт дату массивом переменной длины: [2026, 7, 28],
        // [2026, 7] или [2026]. Длина и есть точность.
        int month = parts.Count > 1 ? parts[1] ?? 1 : 1;
        int day = parts.Count > 2 ? parts[2] ?? 1 : 1;

        TimePrecision precision = parts.Count switch
        {
            >= 3 => TimePrecision.Day,
            2    => TimePrecision.Month,
            _    => TimePrecision.Year,
        };

        long start = TimeAxis.FromGregorian(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 28));
        long end = precision switch
        {
            TimePrecision.Day   => start + 1,
            TimePrecision.Month => TimeAxis.StartOfNextMonth(year, Math.Clamp(month, 1, 12)),
            _                   => TimeAxis.StartOfYear(year + 1),
        };

        if (end <= start) return null;

        string journal = item.ContainerTitle?.FirstOrDefault() ?? "";
        var record = new EventRecord
        {
            WikidataId = $"doi:{item.Doi}",
            Kind       = "publication",
            TStart     = start,
            TEnd       = end,
            Precision  = precision,
            Calendar   = CalendarModel.Gregorian,
            SourceUrl  = $"https://doi.org/{item.Doi}",
            // Значимость свежих статей приходится назначать, а не измерять:
            // цитирования появляются через годы. База 0,2 ставит их ниже
            // хрестоматийных открытий, но выше шума, а цитирования добавляют
            // веса тем, что уже успели прогреметь.
            SignificanceOverride = Score(item.CitationCount),
        };

        record.AddTranslation("en", CleanTitle(title), Describe(item, journal));

        string? category = CategoryFor(journal, item.Subject);
        if (category is not null) record.CategorySlugs.Add(category);

        return record;
    }

    private static float Score(int citations)
    {
        double bonus = Math.Log(1 + citations) / Math.Log(1 + 300);
        return (float)Math.Clamp(0.20 + bonus * 0.5, 0d, 0.75d);
    }

    private static string? CategoryFor(string journal, List<string>? subjects)
    {
        foreach (var (_, name, category) in Journals)
            if (category is not null && string.Equals(name, journal, StringComparison.OrdinalIgnoreCase))
                return category;

        foreach (string subject in subjects ?? [])
            foreach (var (needle, category) in SubjectMap)
                if (subject.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return category;

        return null;
    }

    /// <summary>Заголовки Crossref приходят с разметкой JATS и лишними пробелами.</summary>
    private static string CleanTitle(string title)
        => StripMarkup(title).Trim();

    /// <summary>
    /// Описание собирается только из журнала и авторов.
    ///
    /// Аннотации Crossref сюда сознательно не попадают. Библиографические
    /// метаданные Crossref распространяет как CC0, но аннотации депонируют
    /// издатели и права на них остаются у издателей — в отличие от заголовка
    /// аннотация достаточно длинна и оригинальна, чтобы охраняться авторским
    /// правом. Журнал, авторы и дата — факты, они не охраняются.
    /// </summary>
    private static string Describe(CrossrefItem item, string journal)
    {
        var authors = (item.Author ?? [])
            .Select(a => string.Join(" ", new[] { a.Given, a.Family }.Where(s => !string.IsNullOrWhiteSpace(s))))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(3)
            .ToList();

        string people = authors.Count switch
        {
            0 => "",
            _ => string.Join(", ", authors) + ((item.Author?.Count ?? 0) > 3 ? " et al." : ""),
        };

        return string.Join(". ", new[] { journal, people }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string StripMarkup(string value)
        => Whitespace().Replace(Markup().Replace(value, " "), " ").Trim();

    public void Dispose() => _http.Dispose();
}

// ---- разбор ответа ---------------------------------------------------

file sealed class CrossrefResponse
{
    [JsonPropertyName("message")] public CrossrefMessage? Message { get; set; }
}

public sealed class CrossrefMessage
{
    [JsonPropertyName("items")]       public List<CrossrefItem> Items { get; set; } = [];
    [JsonPropertyName("next-cursor")] public string? NextCursor { get; set; }
}

public sealed class CrossrefItem
{
    [JsonPropertyName("DOI")]                   public string? Doi { get; set; }
    [JsonPropertyName("title")]                 public List<string>? Title { get; set; }
    [JsonPropertyName("published")]             public CrossrefDate? Published { get; set; }
    [JsonPropertyName("issued")]                public CrossrefDate? Issued { get; set; }
    [JsonPropertyName("container-title")]       public List<string>? ContainerTitle { get; set; }
    [JsonPropertyName("is-referenced-by-count")] public int CitationCount { get; set; }
    [JsonPropertyName("subject")]               public List<string>? Subject { get; set; }
    [JsonPropertyName("author")]                public List<CrossrefAuthor>? Author { get; set; }
}

public sealed class CrossrefDate
{
    [JsonPropertyName("date-parts")] public List<List<int?>>? DateParts { get; set; }
}

public sealed class CrossrefAuthor
{
    [JsonPropertyName("given")]  public string? Given { get; set; }
    [JsonPropertyName("family")] public string? Family { get; set; }
}
