using System.Text.Json;
using Npgsql;

namespace ScienceTimeline.Etl;

/// <summary>
/// Выгрузка базы в статические файлы для GitHub Pages.
///
/// Бэкенд и база нужны только на этапе сборки данных. Событий 25 тысяч —
/// это несколько мегабайт, которые браузер грузит один раз и дальше считает
/// бакеты, топ-K и поиск сам. Нарезать данные на тайлы по масштабам смысла
/// нет: весь набор меньше одной фотографии.
///
/// Формат колоночный, а не массив объектов. Тысячи одинаковых ключей
/// "tStart", "kind", "significance" сжимаются плохо, а столбцы однотипных
/// чисел — очень хорошо: разница на этом объёме кратная.
///
/// Числовое ядро отделено от текстов: при смене языка догружается только
/// текстовый файл, а ядро остаётся.
/// </summary>
public sealed class StaticExport(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // Кириллица и иероглифы пишутся как есть: экранирование раздуло бы
        // файл в разы, а gzip его всё равно вернёт к прежнему размеру.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task RunAsync(string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var (order, core) = await ExportCoreAsync(conn, outputDirectory, ct);
        await ExportMetaAsync(conn, outputDirectory, core, ct);
        await ExportTextAsync(conn, outputDirectory, order, ct);

        Report(outputDirectory);
    }

    // ------------------------------------------------------------------

    private sealed class Core
    {
        public List<long> Id { get; } = [];
        public List<long> TStart { get; } = [];
        public List<long> TEnd { get; } = [];
        public List<long> TMid { get; } = [];
        public List<int> Precision { get; } = [];
        public List<int> Kind { get; } = [];
        public List<int> Circa { get; } = [];
        public List<int> Significance { get; } = [];
        public List<int> Categories { get; } = [];
        public List<string?> Url { get; } = [];
        public List<string?> Image { get; } = [];
    }

    /// <summary>
    /// Числовое ядро. Точность, тип и области науки кодируются числами,
    /// а словари для них лежат в meta.json — так строка «publication»
    /// не повторяется двадцать тысяч раз.
    ///
    /// Области науки складываются в битовую маску: категорий одиннадцать,
    /// они помещаются в одно целое, и проверка фильтра на клиенте становится
    /// побитовым И вместо перебора массива строк.
    /// </summary>
    private async Task<(List<long> Order, Core Core)> ExportCoreAsync(
        NpgsqlConnection conn, string directory, CancellationToken ct)
    {
        var precisions = await ReadEnumAsync(conn, "date_precision", ct);
        var kinds = await ReadEnumAsync(conn, "event_kind", ct);
        var categories = await ReadCategorySlugsAsync(conn, ct);

        var core = new Core();
        var order = new List<long>();

        const string sql = """
            select e.id,
                   e.t_start, e.t_end, e.t_mid,
                   e.time_precision::text as precision,
                   e.kind::text as kind,
                   e.circa,
                   e.significance,
                   coalesce(e.wikipedia_en, e.wikipedia_ru, e.source_url) as url,
                   -- Wikidata отдаёт ссылки на Викисклад по http, а сайт работает
                   -- по https: браузер считает такую картинку смешанным содержимым
                   -- и молча её не грузит. Без этой замены изображений нет ни у
                   -- одного события.
                   replace(e.image_url, 'http://', 'https://') as image_url,
                   coalesce((
                       select array_agg(c.slug order by c.slug)
                       from event_categories ec
                       join categories c on c.id = ec.category_id
                       where ec.event_id = e.id
                   ), array[]::text[]) as categories
            from events e
            order by e.t_mid, e.id
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            long id = reader.GetInt64(0);
            order.Add(id);

            core.Id.Add(id);
            core.TStart.Add(reader.GetInt64(1));
            core.TEnd.Add(reader.GetInt64(2));
            core.TMid.Add(reader.GetInt64(3));
            core.Precision.Add(precisions.IndexOf(reader.GetString(4)));
            core.Kind.Add(kinds.IndexOf(reader.GetString(5)));
            core.Circa.Add(reader.GetBoolean(6) ? 1 : 0);

            // Значимость нужна только для сортировки, три знака после запятой
            // её полностью описывают. Целое вместо дроби экономит место.
            core.Significance.Add((int)Math.Round(reader.GetFloat(7) * 1000));

            core.Url.Add(reader.IsDBNull(8) ? null : reader.GetString(8));
            core.Image.Add(reader.IsDBNull(9) ? null : reader.GetString(9));

            int mask = 0;
            foreach (string slug in reader.GetFieldValue<string[]>(10))
            {
                int index = categories.IndexOf(slug);
                if (index >= 0) mask |= 1 << index;
            }
            core.Categories.Add(mask);
        }

        await WriteAsync(Path.Combine(directory, "core.json"), new
        {
            count = core.Id.Count,
            id = core.Id,
            tStart = core.TStart,
            tEnd = core.TEnd,
            tMid = core.TMid,
            precision = core.Precision,
            kind = core.Kind,
            circa = core.Circa,
            significance = core.Significance,
            categories = core.Categories,
            url = core.Url,
            image = core.Image,
        }, ct);

        return (order, core);
    }

    /// <summary>
    /// Тексты по одному файлу на язык, выровненные по тому же порядку,
    /// что и ядро: связь идёт по позиции, идентификаторы не дублируются.
    /// Пустая строка означает, что перевода нет и клиент возьмёт английский.
    /// </summary>
    private async Task ExportTextAsync(
        NpgsqlConnection conn, string directory, List<long> order, CancellationToken ct)
    {
        var position = new Dictionary<long, int>(order.Count);
        for (int i = 0; i < order.Count; i++) position[order[i]] = i;

        var languages = new List<string>();
        await using (var langCmd = new NpgsqlCommand("select distinct lang from event_translations order by lang", conn))
        await using (var langReader = await langCmd.ExecuteReaderAsync(ct))
            while (await langReader.ReadAsync(ct)) languages.Add(langReader.GetString(0));

        foreach (string lang in languages)
        {
            var titles = new string[order.Count];
            var summaries = new string[order.Count];
            Array.Fill(titles, "");
            Array.Fill(summaries, "");

            await using var cmd = new NpgsqlCommand(
                "select event_id, title, coalesce(summary, '') from event_translations where lang = @lang", conn);
            cmd.Parameters.AddWithValue("lang", lang);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!position.TryGetValue(reader.GetInt64(0), out int index)) continue;
                titles[index] = reader.GetString(1);
                summaries[index] = reader.GetString(2);
            }

            await WriteAsync(Path.Combine(directory, $"text-{lang}.json"), new
            {
                lang,
                title = titles,
                summary = summaries,
            }, ct);
        }
    }

    private async Task ExportMetaAsync(NpgsqlConnection conn, string directory, Core core, CancellationToken ct)
    {
        var categories = new List<object>();
        await using (var cmd = new NpgsqlCommand("select slug, name_ru, name_en, color from categories order by id", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                categories.Add(new
                {
                    slug = reader.GetString(0),
                    nameRu = reader.GetString(1),
                    nameEn = reader.GetString(2),
                    color = reader.GetString(3),
                });

        var languages = new List<string>();
        await using (var cmd = new NpgsqlCommand("select distinct lang from event_translations order by lang", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) languages.Add(reader.GetString(0));

        await WriteAsync(Path.Combine(directory, "meta.json"), new
        {
            eventCount = core.Id.Count,
            minTime = core.TMid.Count > 0 ? core.TMid[0] : 0,
            maxTime = core.TMid.Count > 0 ? core.TMid[^1] : 0,
            categories,
            categorySlugs = categories.Select(c => ((dynamic)c).slug).Cast<string>().ToList(),
            kinds = await ReadEnumAsync(conn, "event_kind", ct),
            precisions = await ReadEnumAsync(conn, "date_precision", ct),
            languages,
            generatedAt = DateTime.UtcNow.ToString("O"),
        }, ct);
    }

    // ------------------------------------------------------------------

    private static async Task<List<string>> ReadEnumAsync(NpgsqlConnection conn, string type, CancellationToken ct)
    {
        var values = new List<string>();
        await using var cmd = new NpgsqlCommand($"select unnest(enum_range(null::{type}))::text", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<List<string>> ReadCategorySlugsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var slugs = new List<string>();
        await using var cmd = new NpgsqlCommand("select slug from categories order by id", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) slugs.Add(reader.GetString(0));
        return slugs;
    }

    private static async Task WriteAsync(string path, object value, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
    }

    private static void Report(string directory)
    {
        Console.WriteLine();
        Console.WriteLine($"выгружено в {directory}:");

        long total = 0;
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(f => f.Length))
        {
            total += file.Length;
            Console.WriteLine($"  {file.Name,-16} {file.Length / 1024.0,8:0.0} КБ");
        }

        Console.WriteLine($"  {"итого",-16} {total / 1024.0 / 1024.0,8:0.00} МБ");
    }
}
