namespace ScienceTimeline.Etl;

/// <summary>
/// SPARQL-запросы к Wikidata.
///
/// Импорт устроен в две фазы, и это не стилистика, а следствие лимитов WDQS:
/// любой запрос обязан уложиться в 60 секунд.
///
///   Фаза 1 — отбор. Несколько широких запросов, которые дают только
///   идентификаторы и даты. Порядок тройных шаблонов здесь важен: если
///   поставить фильтр по sitelinks перед p:P575, планировщик начинает
///   со сканирования всех элементов Wikidata и запрос отваливается
///   по таймауту (проверено: 49 с на 872 события против 18 с на 47 826).
///
///   Фаза 2 — обогащение. Метки, описания, картинки, области науки и авторы
///   добираются порциями через VALUES ?item { ... }. Список идентификаторов
///   резко сужает обход графа, поэтому такие запросы отрабатывают за секунды
///   даже с полудюжиной OPTIONAL.
/// </summary>
public static class Queries
{
    // Классы Wikidata, проверенные запросом к самому сервису, а не по памяти.
    public const string AstronomicalObject = "Q6999";
    public const string Invention          = "Q12579633";   // не Q1061398 — то музыкальная форма
    public const string ArtificialObject   = "Q8205328";
    public const string Device             = "Q1183543";
    public const string Machine            = "Q11019";
    public const string PhysicalTool       = "Q39546";

    public const string NobelPhysics   = "Q38104";
    public const string NobelChemistry = "Q44585";
    public const string NobelMedicine  = "Q80061";

    /// <summary>Корни областей науки, к которым сводятся все более узкие темы.</summary>
    public static readonly (string QId, string Slug)[] CategoryRoots =
    [
        ("Q413",   "physics"),
        ("Q2329",  "chemistry"),
        ("Q420",   "biology"),
        ("Q11190", "medicine"),
        ("Q333",   "astronomy"),
        ("Q395",   "mathematics"),
        ("Q21198", "computing"),
        ("Q8008",  "earth"),
        ("Q11023", "engineering"),
        ("Q9418",  "psychology"),
        ("Q34749", "social")
    ];

    /// <summary>
    /// Якорные классы объектов и их области науки.
    ///
    /// Подъём по иерархии подклассов приводит к дисциплине только для тем
    /// («квантовая механика» -> «физика»). Для классов объектов он не работает:
    /// «химический элемент» не является подклассом «химии», это разные ветви
    /// онтологии. Без такого списка треть событий остаётся без области науки —
    /// проверено на прогоне: 365 из 1186.
    ///
    /// Перечислены только верхние классы: «литофильный элемент», «синтетический
    /// элемент» и прочие подхватятся сами, потому что запрос идёт через P279*.
    /// Список составлен по фактическому распределению P31 в данных, а не наугад.
    /// </summary>
    public static readonly (string QId, string Slug)[] TopicAnchors =
    [
        ("Q11344",     "chemistry"),    // химический элемент
        ("Q113145171", "chemistry"),    // тип химической сущности
        ("Q11432",     "chemistry"),    // газ
        ("Q2512777",   "chemistry"),    // простое вещество
        ("Q16521",     "biology"),      // таксон
        ("Q23038290",  "biology"),      // ископаемый таксон
        ("Q112193867", "medicine"),     // тип заболевания
        ("Q22675015",  "physics"),      // тип квантовой частицы
        ("Q1293220",   "physics"),      // физическое явление
        ("Q214070",    "physics"),      // физический закон
        ("Q131299",    "physics"),      // приставка СИ
        ("Q83155725",  "physics"),      // приставка UCUM
        ("Q24034552",  "mathematics"),  // математическое понятие
        ("Q23442",     "earth"),        // остров
        ("Q33837",     "earth"),        // архипелаг
        ("Q1402592",   "earth"),        // группа островов
        ("Q6617741",   "earth"),        // экорегион WWF
        ("Q220659",    "social"),       // археологический артефакт
        ("Q839954",    "social"),       // археологический памятник
        ("Q10855061",  "social"),       // археологическая находка
        ("Q12579633",  "engineering"),  // изобретение
        ("Q124078422", "engineering"),  // тип оружия
        (AstronomicalObject, "astronomy"),

        // Второй заход: добавлено по диагностике прогона, где 2112 событий
        // остались без области науки. Подклассы подтянутся сами — «показательная
        // пещера» через «пещеру», «череп» через анатомию и так далее.
        ("Q35509",     "earth"),        // пещера
        ("Q55818",     "earth"),        // ударный кратер
        ("Q65943",     "mathematics"),  // теорема
        ("Q18347143",  "biology"),      // ископаемые останки гоминин
        ("Q28947902",  "biology"),      // череп
        ("Q860861",    "social"),       // скульптура
        ("Q179700",    "social"),       // статуя
        ("Q381885",    "social"),       // гробница
        ("Q665247",    "social")        // гипогей
    ];

    /// <summary>Все точки привязки к областям науки: и дисциплины, и классы объектов.</summary>
    public static readonly (string QId, string Slug)[] AllCategoryAnchors =
        [.. CategoryRoots, .. TopicAnchors];

    /// <summary>Классы, наличие которых в предках делает событие изобретением, а не открытием.</summary>
    public static readonly string[] InventionRoots =
        [Invention, ArtificialObject, Device, Machine, PhysicalTool];

    private static string Values(IEnumerable<string> qids)
        => string.Join(" ", qids.Select(q => "wd:" + q));

    // -----------------------------------------------------------------
    // Фаза 1 — отбор
    // -----------------------------------------------------------------

    /// <summary>
    /// Все события с датой открытия или изобретения (P575) и не менее
    /// minSitelinks языковых разделов. Астрономические объекты отсюда
    /// не исключаются — их отсекает <see cref="AstronomicalObjectIds"/>,
    /// потому что FILTER NOT EXISTS с обходом P279* здесь не укладывается в лимит.
    /// </summary>
    public static string DiscoveryDates(int minSitelinks) => $$"""
        SELECT ?item ?time ?precision ?calendar ?circa ?sitelinks WHERE {
          ?item p:P575 ?stmt .
          ?stmt psv:P575 ?tv .
          ?tv wikibase:timeValue ?time ;
              wikibase:timePrecision ?precision ;
              wikibase:timeCalendarModel ?calendar .
          ?item wikibase:sitelinks ?sitelinks .
          FILTER(?sitelinks >= {{minSitelinks}})
          OPTIONAL { ?stmt pq:P1480 ?circa }
        }
        """;

    /// <summary>
    /// Классы (P31) для порции элементов — без обхода иерархии.
    ///
    /// Соблазнительно было бы спросить astronomical objects одним запросом
    /// через wdt:P31/wdt:P279* wd:Q6999, но такой запрос возвращает ~40 тысяч
    /// строк, WDQS обрывает поток на середине и присылает битый JSON.
    /// Поэтому классы берутся порциями, а иерархия разворачивается отдельно
    /// на маленьком множестве уникальных классов.
    /// </summary>
    public static string Types(IEnumerable<string> qids) => $$"""
        SELECT ?item ?value WHERE {
          VALUES ?item { {{Values(qids)}} }
          ?item wdt:P31 ?value .
        }
        """;

    /// <summary>Определяет, какие из классов являются астрономическими объектами.</summary>
    public static string ResolveAstronomical(IEnumerable<string> qids) => $$"""
        SELECT DISTINCT ?concept WHERE {
          VALUES ?concept { {{Values(qids)}} }
          ?concept wdt:P279* wd:{{AstronomicalObject}} .
        }
        """;

    /// <summary>
    /// Нобелевские премии по физике, химии и медицине. Год берётся
    /// из квалификатора P585 у утверждения о награде.
    /// </summary>
    public static string NobelPrizes() => $$"""
        SELECT ?person ?prize ?time ?precision ?calendar ?sitelinks WHERE {
          VALUES ?prize { wd:{{NobelPhysics}} wd:{{NobelChemistry}} wd:{{NobelMedicine}} }
          ?person p:P166 ?stmt .
          ?stmt ps:P166 ?prize .
          ?stmt pqv:P585 ?tv .
          ?tv wikibase:timeValue ?time ;
              wikibase:timePrecision ?precision ;
              wikibase:timeCalendarModel ?calendar .
          ?person wikibase:sitelinks ?sitelinks .
        }
        """;

    /// <summary>
    /// Научные теории, законы и принципы. У многих из них нет P575,
    /// зато есть P571 «дата основания или создания».
    ///
    /// Класс берётся только прямой (wdt:P31), без обхода wdt:P279*:
    /// с обходом запрос стабильно отваливается по таймауту WDQS.
    /// Часть узких подклассов при этом теряется — приемлемая плата
    /// за то, что запрос вообще возвращается.
    /// </summary>
    public static string TheoriesAndLaws(int minSitelinks) => $$"""
        SELECT ?item ?time ?precision ?calendar ?circa ?sitelinks WHERE {
          VALUES ?class { wd:Q3239681 wd:Q408891 wd:Q214070 wd:Q17737 }
          ?item wdt:P31 ?class .
          ?item p:P571 ?stmt .
          ?stmt psv:P571 ?tv .
          ?tv wikibase:timeValue ?time ;
              wikibase:timePrecision ?precision ;
              wikibase:timeCalendarModel ?calendar .
          ?item wikibase:sitelinks ?sitelinks .
          FILTER(?sitelinks >= {{minSitelinks}})
          OPTIONAL { ?stmt pq:P1480 ?circa }
        }
        """;

    // -----------------------------------------------------------------
    // Фаза 2 — обогащение порциями
    // -----------------------------------------------------------------

    /// <summary>
    /// Языки интерфейса. Отбирались по числу говорящих с поправкой на то,
    /// насколько язык представлен в самой Wikidata: у бенгальского и урду
    /// носителей больше, чем у немецкого, но меток на них в разы меньше,
    /// и лента вышла бы пустой.
    /// </summary>
    public static readonly string[] Languages =
        ["en", "zh", "hi", "es", "ar", "fr", "pt", "ru", "de", "ja"];

    private static string LanguageFilter(string variable)
        => string.Join(", ", Languages.Select(l => $"\"{l}\"")) is var list
            ? $"FILTER({variable} IN ({list}))"
            : "";

    /// <summary>
    /// Названия и описания на всех языках сразу.
    ///
    /// Одной строкой на язык, а не отдельным OPTIONAL на каждый: десять
    /// языков по два поля дали бы двадцать OPTIONAL в одном запросе,
    /// и WDQS такое не переваривает.
    /// </summary>
    public static string Labels(IEnumerable<string> qids) => $$"""
        SELECT ?item ?field ?lang ?text WHERE {
          VALUES ?item { {{Values(qids)}} }
          {
            ?item rdfs:label ?text .
            BIND("title" AS ?field)
            BIND(LANG(?text) AS ?lang)
            {{LanguageFilter("?lang")}}
          } UNION {
            ?item schema:description ?text .
            BIND("summary" AS ?field)
            BIND(LANG(?text) AS ?lang)
            {{LanguageFilter("?lang")}}
          }
        }
        """;

    /// <summary>Изображение и ссылки на Википедию.</summary>
    public static string Media(IEnumerable<string> qids) => $$"""
        SELECT ?item ?image ?articleRu ?articleEn WHERE {
          VALUES ?item { {{Values(qids)}} }
          OPTIONAL { ?item wdt:P18 ?image }
          OPTIONAL { ?articleRu schema:about ?item ; schema:isPartOf <https://ru.wikipedia.org/> }
          OPTIONAL { ?articleEn schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> }
        }
        """;

    /// <summary>
    /// Связи элемента: область знания, основная тема и автор открытия.
    /// Через UNION, а не через несколько OPTIONAL, чтобы не получить
    /// декартово произведение многозначных свойств.
    /// Классы (P31) сюда не входят — они уже собраны в <see cref="Types"/>
    /// на более раннем шаге, до отсева астрономии.
    /// </summary>
    public static string Relations(IEnumerable<string> qids) => $$"""
        SELECT ?item ?rel ?value WHERE {
          VALUES ?item { {{Values(qids)}} }
          {
            ?item wdt:P101 ?value . BIND("field" AS ?rel)
          } UNION {
            ?item wdt:P921 ?value . BIND("subject" AS ?rel)
          } UNION {
            ?item wdt:P61 ?value . BIND("person" AS ?rel)
          }
        }
        """;

    /// <summary>
    /// Сводит произвольные темы и классы к областям науки, поднимаясь
    /// по цепочке «подкласс чего-либо» до дисциплины или якорного класса.
    /// Порция идентификаторов в VALUES не даёт обходу разрастись.
    /// </summary>
    public static string ResolveCategories(IEnumerable<string> qids) => $$"""
        SELECT DISTINCT ?concept ?root WHERE {
          VALUES ?concept { {{Values(qids)}} }
          VALUES ?root { {{Values(AllCategoryAnchors.Select(c => c.QId))}} }
          ?concept wdt:P279* ?root .
        }
        """;

    /// <summary>Определяет, какие классы являются изобретениями, а не открытиями.</summary>
    public static string ResolveInventionTypes(IEnumerable<string> qids) => $$"""
        SELECT DISTINCT ?concept WHERE {
          VALUES ?concept { {{Values(qids)}} }
          VALUES ?root { {{Values(InventionRoots)}} }
          ?concept wdt:P279* ?root .
        }
        """;

    /// <summary>Имена учёных на всех языках и портрет.</summary>
    public static string People(IEnumerable<string> qids) => $$"""
        SELECT ?item ?lang ?label ?image WHERE {
          VALUES ?item { {{Values(qids)}} }
          ?item rdfs:label ?label .
          BIND(LANG(?label) AS ?lang)
          {{LanguageFilter("?lang")}}
          OPTIONAL { ?item wdt:P18 ?image }
        }
        """;
}
