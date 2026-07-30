using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScienceTimeline.Etl;

/// <summary>Одна ячейка результата SPARQL.</summary>
public sealed class SparqlValue
{
    [JsonPropertyName("type")]     public string? Type { get; set; }
    [JsonPropertyName("value")]    public string? Value { get; set; }
    [JsonPropertyName("datatype")] public string? Datatype { get; set; }
    [JsonPropertyName("xml:lang")] public string? Lang { get; set; }
}

public sealed class SparqlResults
{
    [JsonPropertyName("bindings")]
    public List<Dictionary<string, SparqlValue>> Bindings { get; set; } = [];
}

public sealed class SparqlResponse
{
    [JsonPropertyName("results")]
    public SparqlResults Results { get; set; } = new();
}

/// <summary>
/// Клиент Wikidata Query Service.
///
/// WDQS — общедоступный сервис с жёсткими лимитами: запрос обязан уложиться
/// в 60 секунд, иначе обрывается, а при слишком частых обращениях приходит
/// 429 с заголовком Retry-After. Поэтому здесь есть ретраи с экспоненциальной
/// паузой и обязательный User-Agent: анонимные запросы сервис блокирует.
/// </summary>
public sealed class SparqlClient : IDisposable
{
    private const string Endpoint = "https://query.wikidata.org/sparql";

    private readonly HttpClient _http;
    private readonly int _maxAttempts;

    public SparqlClient(int maxAttempts = 5)
    {
        _maxAttempts = maxAttempts;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.Add("Accept", "application/sparql-results+json");
        // WDQS требует осмысленный User-Agent со способом связи.
        _http.DefaultRequestHeaders.Add(
            "User-Agent",
            "science-timeline/0.1 (https://github.com/science-timeline; ETL for an interactive history-of-science timeline)");
    }

    public async Task<List<Dictionary<string, SparqlValue>>> QueryAsync(
        string sparql,
        CancellationToken ct = default)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("query", sparql)]);
                using var response = await _http.PostAsync(Endpoint, content, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    Console.Error.WriteLine($"  429 от WDQS, жду {wait.TotalSeconds:0} с (попытка {attempt}/{_maxAttempts})");
                    await Task.Delay(wait, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"WDQS вернул {(int)response.StatusCode}: {Truncate(body, 400)}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var parsed = await JsonSerializer.DeserializeAsync<SparqlResponse>(stream, cancellationToken: ct);

                return parsed?.Results.Bindings ?? [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                last = ex;
                if (attempt == _maxAttempts) break;

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2);
                Console.Error.WriteLine($"  сбой запроса ({ex.GetType().Name}: {Truncate(ex.Message, 160)}), повтор через {backoff.TotalSeconds:0} с");
                await Task.Delay(backoff, ct);
            }
        }

        throw new InvalidOperationException(
            $"WDQS не ответил за {_maxAttempts} попыток", last);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

public static class SparqlBindingExtensions
{
    public static string? Str(this Dictionary<string, SparqlValue> row, string key)
        => row.TryGetValue(key, out var v) ? v.Value : null;

    public static int Int(this Dictionary<string, SparqlValue> row, string key, int fallback = 0)
        => row.TryGetValue(key, out var v) && int.TryParse(v.Value, out int n) ? n : fallback;

    public static bool Has(this Dictionary<string, SparqlValue> row, string key)
        => row.ContainsKey(key) && !string.IsNullOrEmpty(row[key].Value);

    /// <summary>Извлекает Q-идентификатор из URI сущности Wikidata.</summary>
    public static string? EntityId(this Dictionary<string, SparqlValue> row, string key)
    {
        string? uri = row.Str(key);
        if (string.IsNullOrEmpty(uri)) return null;

        int slash = uri.LastIndexOf('/');
        return slash >= 0 && slash < uri.Length - 1 ? uri[(slash + 1)..] : uri;
    }
}
