using ScienceTimeline.Etl;

// Импорт истории науки из Wikidata в PostgreSQL.
//
//   dotnet run --project src/ScienceTimeline.Etl -- [ключи]
//
//   --min-sitelinks N   порог значимости, по умолчанию 5
//   --astro-min N       отдельный порог для астрономических объектов, по умолчанию 45
//   --fresh             очистить события и учёных перед импортом
//   --limit N           оставить только N самых значимых событий (быстрая проверка)
//   --chunk N           размер порции при обогащении, по умолчанию 400
//   --no-nobel          пропустить нобелевские премии
//   --no-crossref       пропустить свежие публикации
//   --crossref-since D  с какой даты тянуть публикации, по умолчанию два года назад
//   --crossref-limit N  предел числа публикаций, по умолчанию 25000

// Иначе кириллица в логе превращается в кракозябры под кодовой страницей консоли Windows.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var options = new ImportOptions();
string? exportDirectory = null;

string connectionString =
    Environment.GetEnvironmentVariable("SCIENCE_TIMELINE_DB")
    ?? "Host=127.0.0.1;Port=5432;Database=science_timeline;Username=postgres;Password=postgres";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-sitelinks" when i + 1 < args.Length:
            options = options with { MinSitelinks = int.Parse(args[++i]) };
            break;
        case "--astro-min" when i + 1 < args.Length:
            options = options with { AstronomyMinSitelinks = int.Parse(args[++i]) };
            break;
        case "--limit" when i + 1 < args.Length:
            options = options with { Limit = int.Parse(args[++i]) };
            break;
        case "--chunk" when i + 1 < args.Length:
            options = options with { ChunkSize = int.Parse(args[++i]) };
            break;
        case "--no-nobel":
            options = options with { SkipNobel = true };
            break;
        case "--no-crossref":
            options = options with { SkipCrossref = true };
            break;
        case "--crossref-since" when i + 1 < args.Length:
            options = options with { CrossrefSince = DateOnly.Parse(args[++i]) };
            break;
        case "--crossref-limit" when i + 1 < args.Length:
            options = options with { CrossrefLimit = int.Parse(args[++i]) };
            break;
        case "--fresh":
            options = options with { Fresh = true };
            break;
        case "--export" when i + 1 < args.Length:
            exportDirectory = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("см. комментарий в начале Program.cs");
            return 0;
        default:
            Console.Error.WriteLine($"неизвестный ключ: {args[i]}");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var sparql = new SparqlClient();
var db = new Database(connectionString);

try
{
    // Выгрузка — отдельный режим, а не довесок к импорту: пересобрать
    // статику из уже готовой базы нужно куда чаще, чем ходить в Wikidata.
    if (exportDirectory is not null)
    {
        await new StaticExport(connectionString).RunAsync(exportDirectory, cts.Token);
        return 0;
    }

    await new Importer(sparql, db).RunAsync(options, cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("прервано");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"импорт не удался: {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  причина: {ex.InnerException.Message}");
    return 1;
}
