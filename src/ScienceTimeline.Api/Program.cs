using System.Text.Json.Serialization;
using ScienceTimeline.Api;

// Slim-builder и сериализация через генератор кода, а не рефлексию —
// чтобы позже можно было собрать в Native AOT ради быстрого холодного старта
// на Cloud Run. Полный AOT сейчас не включён: Dapper использует эмиссию кода,
// для него понадобится пакет Dapper.AOT либо голый Npgsql.
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

string connectionString =
    Environment.GetEnvironmentVariable("SCIENCE_TIMELINE_DB")
    ?? "Host=127.0.0.1;Port=5432;Database=science_timeline;Username=postgres;Password=postgres";

builder.Services.AddSingleton(new TimelineRepository(connectionString));

// Фронтенд живёт отдельным сервисом, поэтому браузеру нужен CORS.
const string DevCors = "dev";
builder.Services.AddCors(options =>
{
    options.AddPolicy(DevCors, policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors(DevCors);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/meta", async (TimelineRepository repo, CancellationToken ct)
    => Results.Ok(await repo.GetMetaAsync(ct)));

// Главный запрос ленты. from/to — границы видимого диапазона в сутках
// от 1970-01-01, buckets — во сколько интервалов его резать (обычно
// по числу пикселей ширины, делённому на минимальный шаг между точками),
// topk — сколько событий вернуть из каждого интервала.
app.MapGet("/api/timeline", async (
    long from,
    long to,
    TimelineRepository repo,
    CancellationToken ct,
    int buckets = 200,
    int topk = 8,
    string? kinds = null,
    string? categories = null,
    string? precisions = null,
    string lang = "ru") =>
{
    if (to <= from)
        return Results.BadRequest(new { error = "to должен быть больше from" });

    buckets = Math.Clamp(buckets, 1, 2000);
    topk = Math.Clamp(topk, 1, 50);

    var response = await repo.GetTimelineAsync(
        from, to, buckets, topk,
        Split(kinds), Split(categories), Split(precisions),
        Normalise(lang), ct);

    return Results.Ok(response);
});

// Список событий за интервал. Нужен кластерам, которые не разложить
// приближением: у даты с точностью до дня нет внутридневного времени,
// и все события одного дня стоят в одной точке оси на любом масштабе.
app.MapGet("/api/events", async (
    long from,
    long to,
    TimelineRepository repo,
    CancellationToken ct,
    int limit = 25,
    int offset = 0,
    string? kinds = null,
    string? categories = null,
    string lang = "ru") =>
{
    if (to < from)
        return Results.BadRequest(new { error = "to не может быть меньше from" });

    var list = await repo.ListAsync(
        from, to,
        Math.Clamp(limit, 1, 100), Math.Max(0, offset),
        Split(kinds), Split(categories),
        Normalise(lang), ct);

    return Results.Ok(list);
});

app.MapGet("/api/events/{id:long}", async (
    long id,
    TimelineRepository repo,
    CancellationToken ct,
    string lang = "ru") =>
{
    var detail = await repo.GetEventAsync(id, Normalise(lang), ct);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/search", async (
    string q,
    TimelineRepository repo,
    CancellationToken ct,
    int limit = 20,
    string lang = "ru") =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Ok(new List<TimelineEvent>());

    var results = await repo.SearchAsync(q.Trim(), Math.Clamp(limit, 1, 100), Normalise(lang), ct);
    return Results.Ok(results);
});

app.Run();

static string[] Split(string? value)
    => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

/// <summary>
/// Приводит запрошенный язык к поддерживаемому. Неизвестный код молча
/// становится английским: перевод всё равно подбирается с запасным вариантом,
/// а падать из-за «lang=xx» незачем.
/// </summary>
static string Normalise(string? lang)
{
    if (string.IsNullOrWhiteSpace(lang)) return "en";

    string code = lang.Split('-')[0].ToLowerInvariant();
    return Array.IndexOf(Languages.Supported, code) >= 0 ? code : "en";
}

/// <summary>Языки, на которых импортируются данные. Совпадает с Queries.Languages в ETL.</summary>
static class Languages
{
    public static readonly string[] Supported =
        ["en", "zh", "hi", "es", "ar", "fr", "pt", "ru", "de", "ja"];
}

[JsonSerializable(typeof(TimelineResponse))]
[JsonSerializable(typeof(MetaResponse))]
[JsonSerializable(typeof(EventDetail))]
[JsonSerializable(typeof(EventListResponse))]
[JsonSerializable(typeof(List<TimelineEvent>))]
internal partial class AppJsonContext : JsonSerializerContext;
