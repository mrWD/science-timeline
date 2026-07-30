namespace ScienceTimeline.Api;

/// <summary>Событие в том виде, в каком его рисует лента.</summary>
public sealed record TimelineEvent(
    long Id,
    string Title,
    string? Summary,
    string Kind,
    long TStart,
    long TEnd,
    long TMid,
    string Precision,
    /// <summary>Приблизительная ли датировка. Подпись «около …» собирает клиент.</summary>
    bool Circa,
    float Significance,
    string? ImageUrl,
    string? Url,
    string[] Categories);

/// <summary>
/// Один интервал ленты. Total — сколько событий в него попало всего,
/// ByKind — разбивка по типам для подписи кластера, Top — те, что
/// действительно поместятся на экран.
/// </summary>
public sealed record TimelineBucket(
    int Index,
    long Start,
    long End,
    /// <summary>
    /// Фактические границы данных внутри бакета. Отличаются от Start/End,
    /// которые считаются делением диапазона и при узких бакетах округляются
    /// до соседних суток. Для запроса списка нужны именно эти: иначе
    /// в кружке «295 событий» открылся бы список из 390.
    /// </summary>
    long TMin,
    long TMax,
    int Total,
    Dictionary<string, int> ByKind,
    List<TimelineEvent> Top);

public sealed record TimelineResponse(
    long From,
    long To,
    int Buckets,
    double BucketWidth,
    int TotalEvents,
    List<TimelineBucket> Items);

/// <summary>
/// Список событий за интервал с постраничным доступом.
///
/// Нужен для кластеров, которые нельзя разложить приближением: у события
/// с точностью до дня нет внутридневного времени, поэтому все статьи одного
/// дня стоят в одной точке оси и остаются одним кружком на любом масштабе.
/// Единственный способ их показать — списком.
/// </summary>
public sealed record EventListResponse(int Total, List<TimelineEvent> Items);

public sealed record CategoryDto(short Id, string Slug, string NameRu, string NameEn, string Color);

public sealed record MetaResponse(
    long MinTime,
    long MaxTime,
    int EventCount,
    List<CategoryDto> Categories,
    string[] Kinds,
    string[] Precisions,
    /// <summary>Языки, на которых в базе есть хотя бы один перевод.</summary>
    string[] Languages);

public sealed record PersonDto(string? Name, string? ImageUrl, string? WikidataId);

public sealed record EventDateDto(string Role, long TStart, long TEnd, string Precision, string? Display, string? Note);

/// <summary>Полная карточка события.</summary>
public sealed record EventDetail(
    long Id,
    string? WikidataId,
    string Title,
    string? Summary,
    string Kind,
    long TStart,
    long TEnd,
    long TMid,
    string Precision,
    bool Circa,
    string? CalendarOriginal,
    int Sitelinks,
    float Significance,
    string? ImageUrl,
    string? WikipediaRu,
    string? WikipediaEn,
    string? SourceUrl,
    string[] Categories,
    List<PersonDto> People,
    List<EventDateDto> Dates);
