using Dapper;
using Npgsql;

namespace ScienceTimeline.Api;

/// <summary>
/// Доступ к данным ленты. Весь SQL написан руками и живёт здесь.
///
/// Главный запрос делает ровно то, ради чего затевался числовой формат
/// оси времени: берёт диапазон [lo, hi), режет его на nb равных бакетов
/// и одновременно возвращает и счётчики по типам событий (для подписи
/// кластера), и топ-K самых значимых событий в каждом бакете (для точек,
/// которые реально помещаются на экран).
///
/// Ключ к скорости — индекс events (t_mid) include (significance, kind):
/// и отбор диапазона, и сортировка по значимости обслуживаются одним
/// проходом по индексу, без обращения к таблице.
/// </summary>
public sealed class TimelineRepository(string connectionString)
{
    static TimelineRepository()
    {
        // t_start -> TStart и прочее сопоставление snake_case с C#-свойствами.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>
    /// Отбор событий по диапазону и фильтрам плюс разбивка на бакеты.
    /// Вынесено в общее выражение, потому что нужно и для счётчиков,
    /// и для выборки топ-K, а параметры у них одни и те же.
    ///
    /// Пустой массив вместо NULL в фильтрах — сознательно: cardinality(...) = 0
    /// читается однозначно и не заставляет клиента возиться с типизацией
    /// NULL-массивов в Npgsql.
    /// </summary>
    private const string SelectionCte = """
        with params as (
            select @lo::bigint as lo, @hi::bigint as hi, @nb::int as nb
        ),
        sel as (
            select e.id, e.t_mid, e.kind, e.significance
            from events e, params p
            where e.t_mid >= p.lo
              and e.t_mid <  p.hi
              and (cardinality(@kinds::text[]) = 0 or e.kind::text = any(@kinds::text[]))
              and (cardinality(@precisions::text[]) = 0 or e.time_precision::text = any(@precisions::text[]))
              and (cardinality(@categories::text[]) = 0 or exists (
                    select 1
                    from event_categories ec
                    join categories c on c.id = ec.category_id
                    where ec.event_id = e.id
                      and c.slug = any(@categories::text[])
              ))
        ),
        bucketed as (
            select s.id, s.t_mid, s.kind, s.significance,
                   least(p.nb - 1,
                         greatest(0, ((s.t_mid - p.lo) * p.nb) / nullif(p.hi - p.lo, 0)))::int as bucket
            from sel s, params p
        )
        """;

    /// <summary>
    /// Подстановка перевода: запрошенный язык, иначе английский, иначе любой
    /// имеющийся. Боковое соединение с limit 1 отбирает ровно одну строку
    /// на событие, поэтому дублей от нескольких языков не возникает.
    /// </summary>
    private const string TranslationJoin = """
        left join lateral (
            select tr.title, tr.summary
            from event_translations tr
            where tr.event_id = e.id
            order by case tr.lang when @lang then 0 when 'en' then 1 else 2 end, tr.lang
            limit 1
        ) tr on true
        """;

    public async Task<TimelineResponse> GetTimelineAsync(
        long lo, long hi, int buckets, int topK,
        string[] kinds, string[] categories, string[] precisions,
        string lang, CancellationToken ct)
    {
        // source_url в конце: у публикаций из Crossref нет статьи в Википедии,
        // зато есть DOI — а ссылка на первоисточник как раз то, ради чего
        // карточку статьи и открывают.
        string url = lang == "ru"
            ? "coalesce(e.wikipedia_ru, e.wikipedia_en, e.source_url)"
            : "coalesce(e.wikipedia_en, e.wikipedia_ru, e.source_url)";

        string sql = $"""
            {SelectionCte}
            select bucket, kind::text as kind, count(*)::int as n,
                   min(t_mid) as t_min, max(t_mid) as t_max
            from bucketed
            group by bucket, kind
            order by bucket;

            {SelectionCte},
            ranked as (
                select id, bucket,
                       row_number() over (partition by bucket order by significance desc, id) as rn
                from bucketed
            )
            select r.bucket,
                   e.id,
                   coalesce(tr.title, '?') as title,
                   tr.summary,
                   e.kind::text   as kind,
                   e.t_start,
                   e.t_end,
                   e.t_mid,
                   e.time_precision::text as precision,
                   e.circa,
                   e.significance,
                   e.image_url,
                   {url}          as url,
                   coalesce((
                       select array_agg(c.slug order by c.slug)
                       from event_categories ec
                       join categories c on c.id = ec.category_id
                       where ec.event_id = e.id
                   ), array[]::text[]) as categories
            from ranked r
            join events e on e.id = r.id
            {TranslationJoin}
            where r.rn <= @topk
            order by r.bucket, r.rn;
            """;

        var parameters = new
        {
            lo, hi, nb = buckets, topk = topK,
            kinds, categories, precisions, lang
        };

        await using var conn = new NpgsqlConnection(connectionString);
        await using var grid = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));

        var countRows = (await grid.ReadAsync<BucketCountRow>()).ToList();
        var topRows = (await grid.ReadAsync<TopRow>()).ToList();

        return Assemble(lo, hi, buckets, countRows, topRows);
    }

    private static TimelineResponse Assemble(
        long lo, long hi, int buckets,
        List<BucketCountRow> countRows,
        List<TopRow> topRows)
    {
        double width = buckets > 0 ? (hi - lo) / (double)buckets : 0;

        var byKind = new Dictionary<int, Dictionary<string, int>>();
        var totals = new Dictionary<int, int>();
        var tMin = new Dictionary<int, long>();
        var tMax = new Dictionary<int, long>();

        foreach (var row in countRows)
        {
            if (!byKind.TryGetValue(row.Bucket, out var kinds))
                byKind[row.Bucket] = kinds = [];

            kinds[row.Kind] = row.N;
            totals[row.Bucket] = totals.GetValueOrDefault(row.Bucket) + row.N;

            // Строки сгруппированы по (бакет, тип), поэтому границы бакета
            // складываются из границ его типов.
            tMin[row.Bucket] = tMin.TryGetValue(row.Bucket, out long lower) ? Math.Min(lower, row.TMin) : row.TMin;
            tMax[row.Bucket] = tMax.TryGetValue(row.Bucket, out long upper) ? Math.Max(upper, row.TMax) : row.TMax;
        }

        var tops = new Dictionary<int, List<TimelineEvent>>();
        foreach (var row in topRows)
        {
            if (!tops.TryGetValue(row.Bucket, out var list))
                tops[row.Bucket] = list = [];

            list.Add(new TimelineEvent(
                row.Id, row.Title, row.Summary, row.Kind,
                row.TStart, row.TEnd, row.TMid, row.Precision, row.Circa,
                row.Significance, row.ImageUrl, row.Url, row.Categories ?? []));
        }

        // Пустые бакеты не отдаём: при двухстах интервалах и разреженных
        // данных большая их часть пуста, а клиенту от них никакой пользы.
        var items = totals.Keys
            .OrderBy(i => i)
            .Select(i => new TimelineBucket(
                Index: i,
                Start: lo + (long)Math.Floor(i * width),
                End:   lo + (long)Math.Floor((i + 1) * width),
                TMin:  tMin.GetValueOrDefault(i),
                TMax:  tMax.GetValueOrDefault(i),
                Total: totals[i],
                ByKind: byKind.GetValueOrDefault(i) ?? [],
                Top:   tops.GetValueOrDefault(i) ?? []))
            .ToList();

        return new TimelineResponse(lo, hi, buckets, width, totals.Values.Sum(), items);
    }

    /// <summary>
    /// Плоский список событий за интервал, по убыванию значимости.
    ///
    /// Отдельный запрос, а не бакеты с большим topk: здесь нужна не раскладка
    /// по ленте, а перелистывание — у кластера может быть три сотни событий,
    /// и тянуть их разом незачем.
    /// </summary>
    public async Task<EventListResponse> ListAsync(
        long lo, long hi, int limit, int offset,
        string[] kinds, string[] categories,
        string lang, CancellationToken ct)
    {
        // source_url в конце: у публикаций из Crossref нет статьи в Википедии,
        // зато есть DOI — а ссылка на первоисточник как раз то, ради чего
        // карточку статьи и открывают.
        string url = lang == "ru"
            ? "coalesce(e.wikipedia_ru, e.wikipedia_en, e.source_url)"
            : "coalesce(e.wikipedia_en, e.wikipedia_ru, e.source_url)";

        // Границы включительные с обеих сторон: клиент присылает start и end
        // бакета, а у бакета шириной в сутки они совпадают.
        const string where = """
            where e.t_mid >= @lo
              and e.t_mid <= @hi
              and (cardinality(@kinds::text[]) = 0 or e.kind::text = any(@kinds::text[]))
              and (cardinality(@categories::text[]) = 0 or exists (
                    select 1
                    from event_categories ec
                    join categories c on c.id = ec.category_id
                    where ec.event_id = e.id
                      and c.slug = any(@categories::text[])
              ))
            """;

        string sql = $"""
            select count(*)::int from events e
            {where};

            select e.id,
                   coalesce(tr.title, '?') as title,
                   tr.summary,
                   e.kind::text as kind,
                   e.t_start, e.t_end, e.t_mid,
                   e.time_precision::text as precision,
                   e.circa,
                   e.significance, e.image_url,
                   {url} as url,
                   coalesce((
                       select array_agg(c.slug order by c.slug)
                       from event_categories ec
                       join categories c on c.id = ec.category_id
                       where ec.event_id = e.id
                   ), array[]::text[]) as categories
            from events e
            {TranslationJoin}
            {where}
            order by e.significance desc, e.id
            limit @limit offset @offset;
            """;

        await using var conn = new NpgsqlConnection(connectionString);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            sql,
            new { lo, hi, limit, offset, kinds, categories, lang },
            cancellationToken: ct));

        int total = await grid.ReadSingleAsync<int>();
        var rows = (await grid.ReadAsync<TopRow>()).ToList();

        var items = rows.Select(r => new TimelineEvent(
            r.Id, r.Title, r.Summary, r.Kind, r.TStart, r.TEnd, r.TMid,
            r.Precision, r.Circa, r.Significance, r.ImageUrl, r.Url,
            r.Categories ?? [])).ToList();

        return new EventListResponse(total, items);
    }

    public async Task<MetaResponse> GetMetaAsync(CancellationToken ct)
    {
        const string sql = """
            select coalesce(min(t_mid), 0)::bigint as min_time,
                   coalesce(max(t_mid), 0)::bigint as max_time,
                   count(*)::int                   as event_count
            from events;

            select id, slug, name_ru, name_en, color from categories order by id;

            select unnest(enum_range(null::event_kind))::text as kind;

            select unnest(enum_range(null::date_precision))::text as precision;

            select distinct lang from event_translations order by lang;
            """;

        await using var conn = new NpgsqlConnection(connectionString);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(sql, cancellationToken: ct));

        var stats = await grid.ReadSingleAsync<StatsRow>();
        var categories = (await grid.ReadAsync<CategoryDto>()).ToList();
        var kinds = (await grid.ReadAsync<string>()).ToArray();
        var precisions = (await grid.ReadAsync<string>()).ToArray();
        var languages = (await grid.ReadAsync<string>()).ToArray();

        return new MetaResponse(
            stats.MinTime, stats.MaxTime, stats.EventCount, categories, kinds, precisions, languages);
    }

    public async Task<EventDetail?> GetEventAsync(long id, string lang, CancellationToken ct)
    {
        string sql = $"""
            select e.id, e.wikidata_id,
                   coalesce(tr.title, '?') as title,
                   tr.summary,
                   e.kind::text as kind,
                   e.t_start, e.t_end, e.t_mid,
                   e.time_precision::text as precision,
                   e.circa,
                   e.calendar_original, e.sitelinks, e.significance,
                   e.image_url, e.wikipedia_ru, e.wikipedia_en, e.source_url,
                   coalesce((
                       select array_agg(c.slug order by c.slug)
                       from event_categories ec
                       join categories c on c.id = ec.category_id
                       where ec.event_id = e.id
                   ), array[]::text[]) as categories
            from events e
            {TranslationJoin}
            where e.id = @id;

            select {(lang == "en" ? "coalesce(p.name_en, p.name_ru)" : "coalesce(p.name_ru, p.name_en)")} as name,
                   p.image_url, p.wikidata_id
            from event_people ep
            join people p on p.id = ep.person_id
            where ep.event_id = @id
            order by name;

            select d.role::text as role, d.t_start, d.t_end,
                   d.time_precision::text as precision,
                   {(lang == "en" ? "coalesce(d.display_en, d.display_ru)" : "coalesce(d.display_ru, d.display_en)")} as display,
                   {(lang == "en" ? "coalesce(d.note_en, d.note_ru)" : "coalesce(d.note_ru, d.note_en)")} as note
            from event_dates d
            where d.event_id = @id
            order by d.t_start;
            """;

        await using var conn = new NpgsqlConnection(connectionString);
        await using var grid = await conn.QueryMultipleAsync(
            new CommandDefinition(sql, new { id, lang }, cancellationToken: ct));

        var row = await grid.ReadSingleOrDefaultAsync<EventDetailRow>();
        if (row is null) return null;

        var people = (await grid.ReadAsync<PersonDto>()).ToList();
        var dates = (await grid.ReadAsync<EventDateDto>()).ToList();

        return new EventDetail(
            row.Id, row.WikidataId, row.Title, row.Summary, row.Kind,
            row.TStart, row.TEnd, row.TMid, row.Precision, row.Circa,
            row.CalendarOriginal, row.Sitelinks, row.Significance,
            row.ImageUrl, row.WikipediaRu, row.WikipediaEn, row.SourceUrl,
            row.Categories ?? [], people, dates);
    }

    public async Task<List<TimelineEvent>> SearchAsync(string query, int limit, string lang, CancellationToken ct)
    {
        // source_url в конце: у публикаций из Crossref нет статьи в Википедии,
        // зато есть DOI — а ссылка на первоисточник как раз то, ради чего
        // карточку статьи и открывают.
        string url = lang == "ru"
            ? "coalesce(e.wikipedia_ru, e.wikipedia_en, e.source_url)"
            : "coalesce(e.wikipedia_en, e.wikipedia_ru, e.source_url)";

        // Ищем и на языке интерфейса, и на английском: английские названия
        // знают все, а морфология каждого языка разбирает только свои слова.
        // Отбор лучшего совпадения на событие — через distinct on, иначе
        // событие с двумя подходящими переводами вернулось бы дважды.
        string sql = $"""
            with hits as (
                select distinct on (tr.event_id)
                       tr.event_id,
                       ts_rank(tr.search_vector, websearch_to_tsquery(
                           case tr.lang
                               when 'ru' then 'russian'::regconfig
                               when 'en' then 'english'::regconfig
                               when 'es' then 'spanish'::regconfig
                               when 'fr' then 'french'::regconfig
                               when 'de' then 'german'::regconfig
                               when 'pt' then 'portuguese'::regconfig
                               when 'ar' then 'arabic'::regconfig
                               when 'hi' then 'hindi'::regconfig
                               else 'simple'::regconfig
                           end, @query)) as rank
                from event_translations tr
                where tr.lang in (@lang, 'en')
                  and tr.search_vector @@ websearch_to_tsquery(
                          case tr.lang
                              when 'ru' then 'russian'::regconfig
                              when 'en' then 'english'::regconfig
                              when 'es' then 'spanish'::regconfig
                              when 'fr' then 'french'::regconfig
                              when 'de' then 'german'::regconfig
                              when 'pt' then 'portuguese'::regconfig
                              when 'ar' then 'arabic'::regconfig
                              when 'hi' then 'hindi'::regconfig
                              else 'simple'::regconfig
                          end, @query)
                order by tr.event_id, rank desc
            )
            select e.id,
                   coalesce(tr.title, '?') as title,
                   tr.summary,
                   e.kind::text as kind,
                   e.t_start, e.t_end, e.t_mid,
                   e.time_precision::text as precision,
                   e.circa,
                   e.significance, e.image_url,
                   {url} as url,
                   coalesce((
                       select array_agg(c.slug order by c.slug)
                       from event_categories ec
                       join categories c on c.id = ec.category_id
                       where ec.event_id = e.id
                   ), array[]::text[]) as categories
            from hits
            join events e on e.id = hits.event_id
            {TranslationJoin}
            order by hits.rank desc, e.significance desc
            limit @limit;
            """;

        await using var conn = new NpgsqlConnection(connectionString);
        var rows = await conn.QueryAsync<TopRow>(
            new CommandDefinition(sql, new { query, limit, lang }, cancellationToken: ct));

        return rows.Select(r => new TimelineEvent(
            r.Id, r.Title, r.Summary, r.Kind, r.TStart, r.TEnd, r.TMid,
            r.Precision, r.Circa, r.Significance, r.ImageUrl, r.Url,
            r.Categories ?? [])).ToList();
    }

    // ---- строки результата -------------------------------------------

    private sealed record BucketCountRow(int Bucket, string Kind, int N, long TMin, long TMax);

    private sealed record StatsRow(long MinTime, long MaxTime, int EventCount);

    // Классы со свойствами, а не позиционные записи: Dapper подбирает
    // конструктор по точному совпадению типов, а text[] он видит как
    // System.Array и до string[] не доходит. При присваивании свойств
    // приведение проходит без проблем.
    private sealed class TopRow
    {
        public int Bucket { get; set; }
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string? Summary { get; set; }
        public string Kind { get; set; } = "";
        public long TStart { get; set; }
        public long TEnd { get; set; }
        public long TMid { get; set; }
        public string Precision { get; set; } = "";
        public bool Circa { get; set; }
        public float Significance { get; set; }
        public string? ImageUrl { get; set; }
        public string? Url { get; set; }
        public string[]? Categories { get; set; }
    }

    private sealed class EventDetailRow
    {
        public long Id { get; set; }
        public string? WikidataId { get; set; }
        public string Title { get; set; } = "";
        public string? Summary { get; set; }
        public string Kind { get; set; } = "";
        public long TStart { get; set; }
        public long TEnd { get; set; }
        public long TMid { get; set; }
        public string Precision { get; set; } = "";
        public bool Circa { get; set; }
        public string? CalendarOriginal { get; set; }
        public int Sitelinks { get; set; }
        public float Significance { get; set; }
        public string? ImageUrl { get; set; }
        public string? WikipediaRu { get; set; }
        public string? WikipediaEn { get; set; }
        public string? SourceUrl { get; set; }
        public string[]? Categories { get; set; }
    }
}
