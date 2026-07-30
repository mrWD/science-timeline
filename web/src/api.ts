export interface TimelineEvent {
  id: number;
  title: string;
  summary: string | null;
  kind: string;
  tStart: number;
  tEnd: number;
  tMid: number;
  precision: string;
  /** Приблизительная ли датировка — подпись «около …» собирается на клиенте. */
  circa: boolean;
  significance: number;
  imageUrl: string | null;
  url: string | null;
  categories: string[];
}

export interface TimelineBucket {
  index: number;
  start: number;
  end: number;
  /** Фактические границы данных в бакете — start/end округляются до соседних суток. */
  tMin: number;
  tMax: number;
  total: number;
  byKind: Record<string, number>;
  top: TimelineEvent[];
}

export interface TimelineResponse {
  from: number;
  to: number;
  buckets: number;
  bucketWidth: number;
  totalEvents: number;
  items: TimelineBucket[];
}

export interface Category {
  id: number;
  slug: string;
  nameRu: string;
  nameEn: string;
  color: string;
}

export interface Meta {
  minTime: number;
  maxTime: number;
  eventCount: number;
  categories: Category[];
  kinds: string[];
  precisions: string[];
  /** Языки, на которых в базе есть хотя бы один перевод. */
  languages: string[];
}

export interface TimelineQuery {
  from: number;
  to: number;
  buckets: number;
  topk: number;
  kinds?: string[];
  categories?: string[];
  precisions?: string[];
  lang?: string;
}

export async function fetchMeta(): Promise<Meta> {
  const response = await fetch('/api/meta');
  if (!response.ok) throw new Error(`/api/meta вернул ${response.status}`);
  return response.json();
}

/**
 * Запрос ленты. Каждое движение мыши меняет диапазон, поэтому предыдущий
 * запрос отменяется через AbortSignal: иначе ответы приходят вразнобой
 * и лента моргает данными от уже неактуального масштаба.
 */
export async function fetchTimeline(query: TimelineQuery, signal: AbortSignal): Promise<TimelineResponse> {
  const params = new URLSearchParams({
    from: String(Math.floor(query.from)),
    to: String(Math.ceil(query.to)),
    buckets: String(query.buckets),
    topk: String(query.topk),
  });

  if (query.kinds?.length) params.set('kinds', query.kinds.join(','));
  if (query.categories?.length) params.set('categories', query.categories.join(','));
  if (query.precisions?.length) params.set('precisions', query.precisions.join(','));
  if (query.lang) params.set('lang', query.lang);

  const response = await fetch(`/api/timeline?${params}`, { signal });
  if (!response.ok) throw new Error(`/api/timeline вернул ${response.status}`);
  return response.json();
}

export interface EventListResponse {
  total: number;
  items: TimelineEvent[];
}

/** Плоский список событий за интервал — для кластеров, которые не разложить зумом. */
export async function fetchEventList(
  from: number,
  to: number,
  offset: number,
  limit: number,
  lang: string,
  filters: { kinds?: string[]; categories?: string[] },
  signal: AbortSignal,
): Promise<EventListResponse> {
  const params = new URLSearchParams({
    from: String(Math.floor(from)),
    to: String(Math.ceil(to)),
    offset: String(offset),
    limit: String(limit),
    lang,
  });

  if (filters.kinds?.length) params.set('kinds', filters.kinds.join(','));
  if (filters.categories?.length) params.set('categories', filters.categories.join(','));

  const response = await fetch(`/api/events?${params}`, { signal });
  if (!response.ok) throw new Error(`/api/events вернул ${response.status}`);
  return response.json();
}

export async function search(query: string, lang: string, signal: AbortSignal): Promise<TimelineEvent[]> {
  const response = await fetch(
    `/api/search?q=${encodeURIComponent(query)}&limit=15&lang=${encodeURIComponent(lang)}`,
    { signal },
  );
  if (!response.ok) throw new Error(`/api/search вернул ${response.status}`);
  return response.json();
}
