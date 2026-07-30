using Npgsql;
using NpgsqlTypes;
using ScienceTimeline.Core;

namespace ScienceTimeline.Etl;

/// <summary>
/// Запись результатов импорта в PostgreSQL.
///
/// Данные заливаются бинарным COPY во временные таблицы, а уже оттуда
/// одним запросом переносятся в целевые с ON CONFLICT. Так весь импорт —
/// это одна транзакция и несколько запросов вместо десятков тысяч INSERT,
/// и повторный прогон не плодит дубликатов.
///
/// Временные таблицы держат все колонки текстом: бинарный COPY не умеет
/// писать пользовательские enum-типы без предварительной регистрации,
/// а приведение text -> enum при переносе стоит копейки.
/// </summary>
public sealed class Database(string connectionString)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Полная очистка событий и учёных. Справочник категорий не трогается —
    /// он заполняется отдельным seed-скриптом, а не импортом.
    /// CASCADE снимает связи в event_categories, event_people, event_dates
    /// и event_links.
    /// </summary>
    public async Task TruncateAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await ExecAsync(conn, "truncate table events, people restart identity cascade;", ct);
    }

    public async Task<(int Events, int People, int Categories)> WriteAsync(
        IReadOnlyCollection<EventRecord> events,
        IReadOnlyDictionary<string, PersonRecord> people,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, """
            create temp table tmp_events (
                wikidata_id       text primary key,
                kind              text,
                t_start           bigint,
                t_end             bigint,
                time_precision    text,
                circa             boolean,
                calendar_original text,
                sitelinks         integer,
                significance      real,
                image_url         text,
                wikipedia_ru      text,
                wikipedia_en      text,
                source_url        text
            ) on commit drop;

            create temp table tmp_translations (
                wikidata_id text,
                lang        text,
                title       text,
                summary     text
            ) on commit drop;

            create temp table tmp_people (
                wikidata_id text primary key,
                name_ru     text,
                name_en     text,
                image_url   text
            ) on commit drop;

            create temp table tmp_event_categories (
                wikidata_id text,
                slug        text
            ) on commit drop;

            create temp table tmp_event_people (
                event_wikidata_id  text,
                person_wikidata_id text
            ) on commit drop;
            """, ct);

        // ---- COPY событий -------------------------------------------------
        await using (var writer = await conn.BeginBinaryImportAsync(
            """
            copy tmp_events (
                wikidata_id, kind, t_start, t_end, time_precision, circa,
                calendar_original, sitelinks, significance, image_url,
                wikipedia_ru, wikipedia_en, source_url
            ) from stdin (format binary)
            """, ct))
        {
            foreach (var e in events)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(e.WikidataId, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.Kind, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.TStart, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(e.TEnd, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(e.Precision.ToDbValue(), NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.Circa, NpgsqlDbType.Boolean, ct);
                await writer.WriteAsync(e.Calendar.ToDbValue(), NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.Sitelinks, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(e.Significance, NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, e.ImageUrl, ct);
                await WriteNullableAsync(writer, e.WikipediaRu, ct);
                await WriteNullableAsync(writer, e.WikipediaEn, ct);
                await WriteNullableAsync(writer, e.SourceUrl, ct);
            }

            await writer.CompleteAsync(ct);
        }

        // ---- COPY переводов -----------------------------------------------
        await using (var writer = await conn.BeginBinaryImportAsync(
            "copy tmp_translations (wikidata_id, lang, title, summary) from stdin (format binary)", ct))
        {
            foreach (var e in events)
                foreach (var (lang, translation) in e.Translations)
                {
                    // Описание без названия показывать не в чем.
                    if (!translation.HasTitle) continue;

                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(e.WikidataId, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(lang, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(translation.Title!, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, translation.Summary, ct);
                }

            await writer.CompleteAsync(ct);
        }

        // ---- COPY учёных --------------------------------------------------
        await using (var writer = await conn.BeginBinaryImportAsync(
            "copy tmp_people (wikidata_id, name_ru, name_en, image_url) from stdin (format binary)", ct))
        {
            foreach (var p in people.Values.Where(p => p.HasName))
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(p.WikidataId, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, p.Name("ru"), ct);
                await WriteNullableAsync(writer, p.Name("en"), ct);
                await WriteNullableAsync(writer, p.ImageUrl, ct);
            }

            await writer.CompleteAsync(ct);
        }

        // ---- COPY связей --------------------------------------------------
        await using (var writer = await conn.BeginBinaryImportAsync(
            "copy tmp_event_categories (wikidata_id, slug) from stdin (format binary)", ct))
        {
            foreach (var e in events)
                foreach (var slug in e.CategorySlugs)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(e.WikidataId, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(slug, NpgsqlDbType.Text, ct);
                }

            await writer.CompleteAsync(ct);
        }

        await using (var writer = await conn.BeginBinaryImportAsync(
            "copy tmp_event_people (event_wikidata_id, person_wikidata_id) from stdin (format binary)", ct))
        {
            foreach (var e in events)
                foreach (var personId in e.PersonIds.Where(people.ContainsKey))
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(e.WikidataId, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(personId, NpgsqlDbType.Text, ct);
                }

            await writer.CompleteAsync(ct);
        }

        // ---- Перенос в целевые таблицы -------------------------------------
        int eventCount = await ExecAsync(conn, """
            insert into events (
                wikidata_id, kind, t_start, t_end, time_precision, circa,
                calendar_original, sitelinks, significance, image_url,
                wikipedia_ru, wikipedia_en, source_url
            )
            select
                wikidata_id, kind::event_kind, t_start, t_end,
                time_precision::date_precision, circa,
                calendar_original, sitelinks, significance, image_url,
                wikipedia_ru, wikipedia_en, source_url
            from tmp_events
            on conflict (wikidata_id) do update set
                kind              = excluded.kind,
                t_start           = excluded.t_start,
                t_end             = excluded.t_end,
                time_precision    = excluded.time_precision,
                circa             = excluded.circa,
                calendar_original = excluded.calendar_original,
                sitelinks         = excluded.sitelinks,
                significance      = excluded.significance,
                image_url         = excluded.image_url,
                wikipedia_ru      = excluded.wikipedia_ru,
                wikipedia_en      = excluded.wikipedia_en,
                source_url        = excluded.source_url
            """, ct);

        await ExecAsync(conn, """
            insert into event_translations (event_id, lang, title, summary)
            select e.id, t.lang, t.title, t.summary
            from tmp_translations t
            join events e on e.wikidata_id = t.wikidata_id
            on conflict (event_id, lang) do update set
                title   = excluded.title,
                summary = excluded.summary
            """, ct);

        int peopleCount = await ExecAsync(conn, """
            insert into people (wikidata_id, name_ru, name_en, image_url)
            select wikidata_id, name_ru, name_en, image_url from tmp_people
            on conflict (wikidata_id) do update set
                name_ru   = excluded.name_ru,
                name_en   = excluded.name_en,
                image_url = excluded.image_url
            """, ct);

        int categoryCount = await ExecAsync(conn, """
            insert into event_categories (event_id, category_id)
            select distinct e.id, c.id
            from tmp_event_categories t
            join events     e on e.wikidata_id = t.wikidata_id
            join categories c on c.slug = t.slug
            on conflict do nothing
            """, ct);

        await ExecAsync(conn, """
            insert into event_people (event_id, person_id)
            select distinct e.id, p.id
            from tmp_event_people t
            join events e on e.wikidata_id = t.event_wikidata_id
            join people p on p.wikidata_id = t.person_wikidata_id
            on conflict do nothing
            """, ct);

        await tx.CommitAsync(ct);
        return (eventCount, peopleCount, categoryCount);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            await writer.WriteNullAsync(ct);
        else
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
