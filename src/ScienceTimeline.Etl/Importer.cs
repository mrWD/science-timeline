using System.Diagnostics;
using ScienceTimeline.Core;

namespace ScienceTimeline.Etl;

public sealed record ImportOptions
{
    /// <summary>Минимум языковых разделов Википедии, чтобы событие вообще попало на ленту.</summary>
    public int MinSitelinks { get; init; } = 5;

    /// <summary>
    /// Отдельный, куда более высокий порог для астрономических объектов.
    /// Без него лента превращается в каталог: у Wikidata 34 тысячи астероидов
    /// с точной датой открытия против пары тысяч настоящих научных событий.
    ///
    /// Значение 45 выбрано по фактическому распределению, а не на глаз.
    /// Ниже этой отметки астрономия давит числом (6906 объектов против 2698
    /// всех остальных событий), выше — соотношение выравнивается и дальше
    /// переворачивается. При 45 остаются Плутон, комета Галлея, Церера
    /// и прочее, что человек ожидает увидеть в истории науки, — около 570
    /// объектов вместо девяти тысяч каталожных записей.
    /// </summary>
    public int AstronomyMinSitelinks { get; init; } = 45;

    /// <summary>Размер порции идентификаторов в VALUES при обогащении.</summary>
    public int ChunkSize { get; init; } = 400;

    /// <summary>
    /// Порция для запроса классов. Он состоит из одного тройного шаблона
    /// и переваривает куда больше идентификаторов за раз, чем обогащение
    /// с полудюжиной OPTIONAL.
    /// </summary>
    public int TypeChunkSize { get; init; } = 1500;

    /// <summary>Ограничение числа событий — для быстрой проверки прогона.</summary>
    public int? Limit { get; init; }

    public bool SkipNobel { get; init; }

    public bool SkipCrossref { get; init; }

    /// <summary>
    /// С какой даты тянуть публикации. Год по умолчанию: выбранные журналы
    /// дают около двух тысяч статей в месяц, и более широкое окно раздувает
    /// базу сверх бесплатного тарифа, ничего не добавляя к главному —
    /// плотности ленты у сегодняшнего дня.
    /// </summary>
    public DateOnly CrossrefSince { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1));

    public int CrossrefLimit { get; init; } = 20_000;

    /// <summary>
    /// Очистить события и учёных перед импортом.
    ///
    /// Обычный прогон только добавляет и обновляет записи, поэтому после
    /// ужесточения порогов в базе остались бы события, которые новый прогон
    /// уже не отбирает. Данные полностью выводимы из Wikidata, так что
    /// пересборка с нуля безопасна — но делается только по явному ключу.
    /// </summary>
    public bool Fresh { get; init; }
}

public sealed class Importer(SparqlClient sparql, Database db)
{
    /// <summary>
    /// Названия премий на всех языках интерфейса. В Wikidata у нобелевских
    /// событий нет собственных элементов — заголовок собирается из названия
    /// премии, года и имени лауреата, поэтому переводить его приходится здесь.
    /// </summary>
    private static readonly Dictionary<string, (Dictionary<string, string> Names, string Slug)> NobelPrizes = new()
    {
        [Queries.NobelPhysics] = (new()
        {
            ["en"] = "Nobel Prize in Physics",
            ["ru"] = "Нобелевская премия по физике",
            ["zh"] = "诺贝尔物理学奖",
            ["hi"] = "भौतिकी का नोबेल पुरस्कार",
            ["es"] = "Premio Nobel de Física",
            ["ar"] = "جائزة نوبل في الفيزياء",
            ["fr"] = "Prix Nobel de physique",
            ["pt"] = "Prémio Nobel de Física",
            ["de"] = "Nobelpreis für Physik",
            ["ja"] = "ノーベル物理学賞",
        }, "physics"),

        [Queries.NobelChemistry] = (new()
        {
            ["en"] = "Nobel Prize in Chemistry",
            ["ru"] = "Нобелевская премия по химии",
            ["zh"] = "诺贝尔化学奖",
            ["hi"] = "रसायन विज्ञान का नोबेल पुरस्कार",
            ["es"] = "Premio Nobel de Química",
            ["ar"] = "جائزة نوبل في الكيمياء",
            ["fr"] = "Prix Nobel de chimie",
            ["pt"] = "Prémio Nobel de Química",
            ["de"] = "Nobelpreis für Chemie",
            ["ja"] = "ノーベル化学賞",
        }, "chemistry"),

        [Queries.NobelMedicine] = (new()
        {
            ["en"] = "Nobel Prize in Physiology or Medicine",
            ["ru"] = "Нобелевская премия по медицине",
            ["zh"] = "诺贝尔生理学或医学奖",
            ["hi"] = "चिकित्सा का नोबेल पुरस्कार",
            ["es"] = "Premio Nobel de Medicina",
            ["ar"] = "جائزة نوبل في الطب",
            ["fr"] = "Prix Nobel de médecine",
            ["pt"] = "Prémio Nobel de Medicina",
            ["de"] = "Nobelpreis für Medizin",
            ["ja"] = "ノーベル生理学・医学賞",
        }, "medicine"),
    };

    private readonly Dictionary<string, EventRecord> _events = [];
    private readonly Dictionary<string, PersonRecord> _people = [];

    /// <summary>Нобелевские события: заголовок собирается только после того, как узнаем имя лауреата.</summary>
    private readonly List<(EventRecord Event, string PersonId, string PrizeId)> _nobelEvents = [];

    public async Task RunAsync(ImportOptions options, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();

        await FetchDiscoveriesAsync(options, ct);
        await FetchTheoriesAsync(options, ct);
        if (!options.SkipNobel) await FetchNobelAsync(ct);
        if (!options.SkipCrossref) await FetchCrossrefAsync(options, ct);

        // Классы нужны раньше обогащения: они отсеивают десятки тысяч
        // астероидов, за которыми иначе пришлось бы ходить за названиями.
        await FetchTypesAsync(options, ct);
        await FilterAstronomyAsync(options, ct);

        ApplyLimit(options);

        await EnrichLabelsAsync(options, ct);
        await EnrichRelationsAsync(options, ct);
        await ResolveCategoriesAndKindsAsync(options, ct);
        await EnrichPeopleAsync(options, ct);

        FinishNobelTitles();
        DropUnusable();

        if (options.Fresh)
        {
            Log("очищаю таблицы событий и учёных (--fresh)");
            await db.TruncateAsync(ct);
        }

        Log($"записываю в БД: {_events.Count} событий, {_people.Count} учёных");
        var (written, people, categories) = await db.WriteAsync(_events.Values, _people, ct);

        Log($"готово за {total.Elapsed.TotalSeconds:0} с — событий {written}, учёных {people}, связей с областями {categories}");
        PrintSummary();
    }

    // ------------------------------------------------------------------
    // Фаза 1 — отбор
    // ------------------------------------------------------------------

    private async Task FetchDiscoveriesAsync(ImportOptions options, CancellationToken ct)
    {
        Log($"запрашиваю открытия и изобретения (от {options.MinSitelinks} языковых разделов)…");
        var rows = await sparql.QueryAsync(Queries.DiscoveryDates(options.MinSitelinks), ct);
        Log($"  получено строк: {rows.Count}");

        int rejected = 0;
        foreach (var row in rows)
        {
            string? id = row.EntityId("item");
            if (id is null) continue;

            if (!TryParseTime(row, out var time, out bool circa)) { rejected++; continue; }

            Upsert(id, row.Int("sitelinks"), time, circa, "discovery");
        }

        Log($"  событий: {_events.Count}, отброшено по дате: {rejected}");
    }

    /// <summary>Классы всех событий. Порциями — целиком такой запрос WDQS не отдаёт.</summary>
    private async Task FetchTypesAsync(ImportOptions options, CancellationToken ct)
    {
        var ids = RealWikidataIds();
        Log($"запрашиваю классы для {ids.Count} событий…");

        int done = 0;
        foreach (var chunk in ids.Chunk(options.TypeChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.Types(chunk), ct);

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                string? value = row.EntityId("value");
                if (id is null || value is null || !_events.TryGetValue(id, out var e)) continue;

                e.Types.Add(value);
                e.Concepts.Add(value);
            }

            done += chunk.Length;
            Log($"  {done}/{ids.Count}");
        }
    }

    /// <summary>
    /// Отделяет историю науки от астрономического каталога.
    ///
    /// У Wikidata 34 тысячи астероидов с точной датой открытия против пары
    /// тысяч настоящих научных событий. Если их не разделить, лента на 95%
    /// состоит из безымянных малых планет. Поэтому астрономические объекты
    /// не выбрасываются, а проходят по отдельному, куда более высокому порогу
    /// значимости: Плутон, комета Галлея и Церера остаются, «1998 QE2» — нет.
    /// </summary>
    private async Task FilterAstronomyAsync(ImportOptions options, CancellationToken ct)
    {
        var types = _events.Values.SelectMany(e => e.Types).ToHashSet();
        Log($"определяю астрономические объекты среди {types.Count} классов…");

        var astroTypes = new HashSet<string>();
        foreach (var chunk in types.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.ResolveAstronomical(chunk), ct);

            foreach (var row in rows)
                if (row.EntityId("concept") is { } concept)
                    astroTypes.Add(concept);
        }

        Log($"  астрономических классов: {astroTypes.Count}");

        int dropped = 0, kept = 0;
        foreach (var (id, e) in _events.ToList())
        {
            if (!e.Types.Any(astroTypes.Contains)) continue;

            if (e.Sitelinks < options.AstronomyMinSitelinks)
            {
                _events.Remove(id);
                dropped++;
            }
            else
            {
                e.CategorySlugs.Add("astronomy");
                kept++;
            }
        }

        Log($"  отсеяно малозначимых астрономических объектов: {dropped}, оставлено значимых: {kept}");
        Log($"  осталось событий: {_events.Count}");
    }

    /// <summary>
    /// Идентификаторы, которые действительно есть в Wikidata.
    ///
    /// Проверка именно на Q-номер, а не на отсутствие решётки: кроме
    /// синтетических нобелевских ключей вида «Q76#nobel-…» в том же словаре
    /// лежат публикации Crossref с ключом «doi:10.1038/…», и спрашивать
    /// у Wikidata метки для DOI бессмысленно.
    /// </summary>
    private List<string> RealWikidataIds()
        => _events.Keys.Where(k => k.StartsWith('Q') && !k.Contains('#')).ToList();

    private async Task FetchTheoriesAsync(ImportOptions options, CancellationToken ct)
    {
        Log("запрашиваю научные теории и законы…");
        try
        {
            var rows = await sparql.QueryAsync(Queries.TheoriesAndLaws(options.MinSitelinks), ct);
            int before = _events.Count;

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                if (id is null || !TryParseTime(row, out var time, out bool circa)) continue;

                Upsert(id, row.Int("sitelinks"), time, circa, "publication");
            }

            Log($"  добавлено: {_events.Count - before}");
        }
        catch (Exception ex)
        {
            // Этот запрос обходит P279* и иногда не укладывается в лимит WDQS.
            // Теории — приятное дополнение, а не основа ленты, поэтому не роняем импорт.
            Log($"  пропускаю: {ex.Message}");
        }
    }

    private async Task FetchNobelAsync(CancellationToken ct)
    {
        Log("запрашиваю нобелевские премии…");
        try
        {
            var rows = await sparql.QueryAsync(Queries.NobelPrizes(), ct);

            foreach (var row in rows)
            {
                string? personId = row.EntityId("person");
                string? prizeId  = row.EntityId("prize");
                if (personId is null || prizeId is null) continue;
                if (!NobelPrizes.ContainsKey(prizeId)) continue;
                if (!TryParseTime(row, out var time, out _)) continue;

                // Премия — это не сам лауреат, поэтому идентификатор синтетический:
                // один человек может получить несколько премий в разные годы.
                var year = TimeAxis.ToGregorian(time.Start).Year;
                string eventId = $"{personId}#nobel-{prizeId}-{year}";

                var record = new EventRecord
                {
                    WikidataId = eventId,
                    Kind       = "award",
                    TStart     = time.Start,
                    TEnd       = time.End,
                    Precision  = time.Precision,
                    Calendar   = time.Calendar,
                    Sitelinks  = row.Int("sitelinks"),
                };

                record.PersonIds.Add(personId);
                record.CategorySlugs.Add(NobelPrizes[prizeId].Slug);

                if (_events.TryAdd(eventId, record))
                    _nobelEvents.Add((record, personId, prizeId));
            }

            Log($"  добавлено премий: {_nobelEvents.Count}");
        }
        catch (Exception ex)
        {
            Log($"  пропускаю: {ex.Message}");
        }
    }

    /// <summary>
    /// Свежие публикации из ведущих журналов. Без них лента обрывается
    /// за годы до сегодняшнего дня: Wikidata узнаёт об открытии сильно позже,
    /// чем оно происходит.
    /// </summary>
    private async Task FetchCrossrefAsync(ImportOptions options, CancellationToken ct)
    {
        Log($"запрашиваю публикации из Crossref с {options.CrossrefSince:yyyy-MM-dd}…");

        try
        {
            using var crossref = new CrossrefSource();
            var records = await crossref.FetchAsync(options.CrossrefSince, options.CrossrefLimit, ct);

            int added = 0;
            foreach (var record in records)
                if (_events.TryAdd(record.WikidataId, record)) added++;

            Log($"  добавлено публикаций: {added}");
        }
        catch (Exception ex)
        {
            Log($"  пропускаю: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Фаза 2 — обогащение
    // ------------------------------------------------------------------

    private async Task EnrichLabelsAsync(ImportOptions options, CancellationToken ct)
    {
        // У нобелевских событий синтетические идентификаторы, в Wikidata их нет.
        var ids = RealWikidataIds();
        Log($"дополняю названиями на {Queries.Languages.Length} языках: {ids.Count} элементов…");

        int done = 0;
        foreach (var chunk in ids.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.Labels(chunk), ct);

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                string? lang = row.Str("lang");
                string? text = row.Str("text");
                if (id is null || lang is null || text is null || !_events.TryGetValue(id, out var e)) continue;

                if (row.Str("field") == "title")
                    e.AddTranslation(lang, text, null);
                else
                    e.AddTranslation(lang, null, text);
            }

            done += chunk.Length;
            Log($"  {done}/{ids.Count}");
        }

        Log($"дополняю картинками и ссылками: {ids.Count} элементов…");
        done = 0;

        foreach (var chunk in ids.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.Media(chunk), ct);

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                if (id is null || !_events.TryGetValue(id, out var e)) continue;

                e.ImageUrl    ??= Secure(row.Str("image"));
                e.WikipediaRu ??= row.Str("articleRu");
                e.WikipediaEn ??= row.Str("articleEn");
            }

            done += chunk.Length;
            Log($"  {done}/{ids.Count}");
        }
    }

    private async Task EnrichRelationsAsync(ImportOptions options, CancellationToken ct)
    {
        var ids = RealWikidataIds();
        Log($"дополняю областями науки и авторами: {ids.Count} элементов…");

        int done = 0;
        foreach (var chunk in ids.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.Relations(chunk), ct);

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                string? value = row.EntityId("value");
                string? rel = row.Str("rel");
                if (id is null || value is null || !_events.TryGetValue(id, out var e)) continue;

                if (rel == "person")
                    e.PersonIds.Add(value);
                else
                    e.Concepts.Add(value);
            }

            done += chunk.Length;
            Log($"  {done}/{ids.Count}");
        }
    }

    private async Task ResolveCategoriesAndKindsAsync(ImportOptions options, CancellationToken ct)
    {
        var concepts = _events.Values.SelectMany(e => e.Concepts).ToHashSet();
        var types    = _events.Values.SelectMany(e => e.Types).ToHashSet();

        Log($"свожу {concepts.Count} тем к областям науки…");

        var conceptToSlug = new Dictionary<string, HashSet<string>>();
        var rootToSlug = Queries.AllCategoryAnchors
            .GroupBy(c => c.QId)
            .ToDictionary(g => g.Key, g => g.First().Slug);

        int done = 0;
        foreach (var chunk in concepts.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.ResolveCategories(chunk), ct);

            foreach (var row in rows)
            {
                string? concept = row.EntityId("concept");
                string? root    = row.EntityId("root");
                if (concept is null || root is null || !rootToSlug.TryGetValue(root, out var slug)) continue;

                if (!conceptToSlug.TryGetValue(concept, out var set))
                    conceptToSlug[concept] = set = [];
                set.Add(slug);
            }

            done += chunk.Length;
            Log($"  {done}/{concepts.Count}");
        }

        Log($"определяю изобретения среди {types.Count} классов…");
        var inventionTypes = new HashSet<string>();

        done = 0;
        foreach (var chunk in types.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.ResolveInventionTypes(chunk), ct);

            foreach (var row in rows)
                if (row.EntityId("concept") is { } concept)
                    inventionTypes.Add(concept);

            done += chunk.Length;
            Log($"  {done}/{types.Count}");
        }

        Log($"  классов-изобретений: {inventionTypes.Count}");

        foreach (var e in _events.Values)
        {
            foreach (var concept in e.Concepts)
                if (conceptToSlug.TryGetValue(concept, out var slugs))
                    e.CategorySlugs.UnionWith(slugs);

            // Тип события меняем только у открытий: у премий и публикаций
            // он уже определён источником.
            if (e.Kind == "discovery" && e.Types.Any(inventionTypes.Contains))
                e.Kind = "invention";
        }
    }

    private async Task EnrichPeopleAsync(ImportOptions options, CancellationToken ct)
    {
        var ids = _events.Values.SelectMany(e => e.PersonIds).ToHashSet();
        Log($"дополняю учёными: {ids.Count}…");

        int done = 0;
        foreach (var chunk in ids.Chunk(options.ChunkSize))
        {
            var rows = await sparql.QueryAsync(Queries.People(chunk), ct);

            foreach (var row in rows)
            {
                string? id = row.EntityId("item");
                if (id is null) continue;

                if (!_people.TryGetValue(id, out var person))
                    _people[id] = person = new PersonRecord { WikidataId = id };

                string? lang = row.Str("lang");
                string? name = row.Str("label");
                if (lang is not null && name is not null) person.Names.TryAdd(lang, name);

                person.ImageUrl ??= row.Str("image");
            }

            done += chunk.Length;
            Log($"  {done}/{ids.Count}");
        }
    }

    // ------------------------------------------------------------------
    // Доводка
    // ------------------------------------------------------------------

    private void FinishNobelTitles()
    {
        foreach (var (record, personId, prizeId) in _nobelEvents)
        {
            var (names, _) = NobelPrizes[prizeId];
            _people.TryGetValue(personId, out var person);

            long year = TimeAxis.ToGregorian(record.TStart).Year;

            foreach (string lang in Queries.Languages)
            {
                string prize = names.TryGetValue(lang, out var localised) ? localised : names["en"];
                string? name = person?.Name(lang);

                record.AddTranslation(lang, name is null ? $"{prize}, {year}" : $"{prize}, {year} — {name}", null);
            }

            record.ImageUrl ??= person?.ImageUrl;
        }
    }

    /// <summary>
    /// Событие без названия нечего показывать в карточке, а без интервала —
    /// негде рисовать. Такие записи не дошли бы до БД из-за ограничений схемы,
    /// поэтому отсеиваем их здесь и говорим об этом вслух.
    /// </summary>
    private void DropUnusable()
    {
        var bad = _events.Where(kv => !kv.Value.HasTitle || kv.Value.TEnd <= kv.Value.TStart)
                         .Select(kv => kv.Key)
                         .ToList();

        foreach (string id in bad) _events.Remove(id);

        if (bad.Count > 0)
            Log($"отброшено без названия или с пустым интервалом: {bad.Count}");
    }

    private void ApplyLimit(ImportOptions options)
    {
        if (options.Limit is not { } limit || _events.Count <= limit) return;

        // Оставляем самые значимые — так на урезанном прогоне лента
        // выглядит осмысленно, а не случайной выборкой.
        var keep = _events.Values
            .OrderByDescending(e => e.Sitelinks)
            .Take(limit)
            .Select(e => e.WikidataId)
            .ToHashSet();

        foreach (string id in _events.Keys.Where(k => !keep.Contains(k)).ToList())
            _events.Remove(id);

        _nobelEvents.RemoveAll(n => !_events.ContainsKey(n.Event.WikidataId));
        Log($"ограничение --limit: оставлено {_events.Count} самых значимых событий");
    }

    // ------------------------------------------------------------------

    private void Upsert(string id, int sitelinks, ParsedTime time, bool circa, string kind)
    {
        var record = new EventRecord
        {
            WikidataId = id,
            Kind       = kind,
            TStart     = time.Start,
            TEnd       = time.End,
            Precision  = time.Precision,
            Calendar   = time.Calendar,
            Circa      = circa,
            Sitelinks  = sitelinks,
        };

        // У элемента может быть несколько утверждений о дате открытия.
        // Оставляем самое точное, при равной точности — самое раннее.
        if (_events.TryGetValue(id, out var existing))
        {
            if (record.IsBetterDateThan(existing))
            {
                record.CategorySlugs.UnionWith(existing.CategorySlugs);
                record.PersonIds.UnionWith(existing.PersonIds);
                _events[id] = record;
            }
        }
        else
        {
            _events[id] = record;
        }
    }

    /// <summary>
    /// Wikidata отдаёт ссылки на Викисклад по http. Сайт работает по https,
    /// и такая картинка становится смешанным содержимым — браузер молча
    /// отказывается её грузить, оставляя пустой прямоугольник в карточке.
    /// </summary>
    private static string? Secure(string? url)
        => url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
            ? string.Concat("https://", url.AsSpan("http://".Length))
            : url;

    private static bool TryParseTime(Dictionary<string, SparqlValue> row, out ParsedTime time, out bool circa)
    {
        circa = row.Has("circa");
        return WikidataTime.TryParse(
            row.Str("time"),
            row.Int("precision", -1),
            row.Str("calendar"),
            circa,
            out time);
    }

    private void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("по типам:");
        foreach (var g in _events.Values.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");

        Console.WriteLine("по точности даты:");
        foreach (var g in _events.Values.GroupBy(e => e.Precision).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");

        Console.WriteLine("по областям науки:");
        foreach (var g in _events.Values.SelectMany(e => e.CategorySlugs).GroupBy(s => s).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");

        var uncategorized = _events.Values.Where(e => e.CategorySlugs.Count == 0).ToList();
        Console.WriteLine($"без области науки: {uncategorized.Count} из {_events.Count}");

        // Подсказка для настройки TopicAnchors: какие классы чаще всего
        // остаются несопоставленными. Без неё список якорей пришлось бы
        // пополнять наугад.
        var topMissing = uncategorized
            .SelectMany(e => e.Types)
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .ToList();

        if (topMissing.Count > 0)
        {
            Console.WriteLine("чаще всего не сопоставились классы (кандидаты в Queries.TopicAnchors):");
            foreach (var g in topMissing)
                Console.WriteLine($"  {g.Key,-12} {g.Count(),6}");
        }
    }

    private static void Log(string message)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
