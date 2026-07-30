/**
 * Данные ленты целиком в браузере.
 *
 * Бэкенда у сайта нет: события выгружены в статические файлы на этапе сборки,
 * а бакетинг, отбор топ-K и поиск считаются здесь. Двадцать пять тысяч событий
 * — это пара мегабайт и обход массива за доли миллисекунды, ради такого объёма
 * держать сервер незачем.
 *
 * Формат колоночный: параллельные массивы вместо массива объектов. Так файл
 * заметно меньше (не повторяются ключи), а обход по диапазону идёт по
 * типизированным массивам без разыменования объектов.
 *
 * События отсортированы по tMid, поэтому выбор видимого диапазона — двоичный
 * поиск двух границ, а не фильтрация всего массива.
 */

export interface TimelineEvent {
  id: number;
  title: string;
  summary: string | null;
  kind: string;
  tStart: number;
  tEnd: number;
  tMid: number;
  precision: string;
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
  categorySlugs: string[];
  kinds: string[];
  precisions: string[];
  languages: string[];
  generatedAt: string;
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

export interface EventListResponse {
  total: number;
  items: TimelineEvent[];
}

interface Core {
  count: number;
  id: number[];
  tStart: number[];
  tEnd: number[];
  tMid: number[];
  precision: number[];
  kind: number[];
  circa: number[];
  significance: number[];
  categories: number[];
  url: (string | null)[];
  image: (string | null)[];
}

interface Text {
  title: string[];
  summary: string[];
}

const BASE = `${import.meta.env.BASE_URL}data`;
const FALLBACK_LANG = 'en';

let meta: Meta | null = null;
let core: Core | null = null;
const texts = new Map<string, Text>();
const pending = new Map<string, Promise<void>>();

async function json<T>(name: string): Promise<T> {
  const response = await fetch(`${BASE}/${name}`);
  if (!response.ok) throw new Error(`${name} вернул ${response.status}`);
  return response.json();
}

/** Загружает тексты языка один раз; повторные вызовы ждут ту же загрузку. */
async function ensureText(lang: string): Promise<void> {
  if (texts.has(lang)) return;

  let task = pending.get(lang);
  if (!task) {
    task = json<Text>(`text-${lang}.json`)
      .then((text) => { texts.set(lang, text); })
      .catch(() => { /* языка нет — обойдёмся запасным */ })
      .finally(() => { pending.delete(lang); });
    pending.set(lang, task);
  }

  await task;
}

export async function fetchMeta(): Promise<Meta> {
  if (!meta) {
    const [loadedMeta, loadedCore] = await Promise.all([json<Meta>('meta.json'), json<Core>('core.json')]);
    meta = loadedMeta;
    core = loadedCore;
    await ensureText(FALLBACK_LANG);
  }
  return meta;
}

/** Английский держим загруженным всегда: он подставляется, когда перевода нет. */
export async function ensureLanguage(lang: string): Promise<void> {
  await ensureText(FALLBACK_LANG);
  if (lang !== FALLBACK_LANG) await ensureText(lang);
}

// ---------------------------------------------------------------------
// Чтение отдельного события
// ---------------------------------------------------------------------

function slugsOf(mask: number): string[] {
  const slugs = meta!.categorySlugs;
  const out: string[] = [];

  for (let bit = 0; bit < slugs.length; bit++)
    if (mask & (1 << bit)) out.push(slugs[bit]!);

  return out;
}

function textAt(index: number, lang: string): { title: string; summary: string | null } {
  const primary = texts.get(lang);
  const fallback = texts.get(FALLBACK_LANG);

  const title = primary?.title[index] || fallback?.title[index] || '';
  const summary = primary?.summary[index] || fallback?.summary[index] || '';

  return { title, summary: summary || null };
}

function eventAt(index: number, lang: string): TimelineEvent {
  const c = core!;
  const { title, summary } = textAt(index, lang);

  return {
    id: c.id[index]!,
    title,
    summary,
    kind: meta!.kinds[c.kind[index]!] ?? 'other',
    tStart: c.tStart[index]!,
    tEnd: c.tEnd[index]!,
    tMid: c.tMid[index]!,
    precision: meta!.precisions[c.precision[index]!] ?? 'unknown',
    circa: c.circa[index] === 1,
    significance: c.significance[index]! / 1000,
    imageUrl: c.image[index] ?? null,
    url: c.url[index] ?? null,
    categories: slugsOf(c.categories[index]!),
  };
}

// ---------------------------------------------------------------------
// Выборка диапазона
// ---------------------------------------------------------------------

/** Первый индекс, у которого tMid >= value. Массив отсортирован по tMid. */
function lowerBound(value: number): number {
  const t = core!.tMid;
  let lo = 0;
  let hi = t.length;

  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (t[mid]! < value) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

interface Filters {
  kinds: number[] | null;
  categoryMask: number;
  precisions: number[] | null;
}

function buildFilters(query: { kinds?: string[]; categories?: string[]; precisions?: string[] }): Filters {
  const kinds = query.kinds?.length
    ? query.kinds.map((k) => meta!.kinds.indexOf(k)).filter((i) => i >= 0)
    : null;

  const precisions = query.precisions?.length
    ? query.precisions.map((p) => meta!.precisions.indexOf(p)).filter((i) => i >= 0)
    : null;

  let categoryMask = 0;
  for (const slug of query.categories ?? []) {
    const bit = meta!.categorySlugs.indexOf(slug);
    if (bit >= 0) categoryMask |= 1 << bit;
  }

  return { kinds, categoryMask, precisions };
}

function passes(index: number, filters: Filters): boolean {
  const c = core!;

  if (filters.kinds && !filters.kinds.includes(c.kind[index]!)) return false;
  if (filters.precisions && !filters.precisions.includes(c.precision[index]!)) return false;
  if (filters.categoryMask && (c.categories[index]! & filters.categoryMask) === 0) return false;

  return true;
}

export async function fetchTimeline(query: TimelineQuery, signal: AbortSignal): Promise<TimelineResponse> {
  const lang = query.lang ?? FALLBACK_LANG;
  await ensureLanguage(lang);
  if (signal.aborted) throw new DOMException('aborted', 'AbortError');

  const c = core!;
  const { from, to, buckets, topk } = query;
  const filters = buildFilters(query);
  const span = to - from;

  const counts = new Map<number, Map<number, number>>();
  const totals = new Map<number, number>();
  const bounds = new Map<number, { min: number; max: number }>();
  const candidates = new Map<number, number[]>();

  const start = lowerBound(from);

  for (let i = start; i < c.count; i++) {
    const t = c.tMid[i]!;
    if (t >= to) break;
    if (!passes(i, filters)) continue;

    // Та же формула, что была в SQL: положение внутри диапазона,
    // растянутое на число бакетов.
    const bucket = Math.min(buckets - 1, Math.max(0, Math.floor(((t - from) * buckets) / span)));

    totals.set(bucket, (totals.get(bucket) ?? 0) + 1);

    let byKind = counts.get(bucket);
    if (!byKind) counts.set(bucket, (byKind = new Map()));
    const kind = c.kind[i]!;
    byKind.set(kind, (byKind.get(kind) ?? 0) + 1);

    const bound = bounds.get(bucket);
    if (!bound) bounds.set(bucket, { min: t, max: t });
    else { if (t < bound.min) bound.min = t; if (t > bound.max) bound.max = t; }

    let list = candidates.get(bucket);
    if (!list) candidates.set(bucket, (list = []));
    list.push(i);
  }

  const width = buckets > 0 ? span / buckets : 0;
  const items: TimelineBucket[] = [];

  for (const bucket of [...totals.keys()].sort((a, b) => a - b)) {
    const indices = candidates.get(bucket)!;

    // Топ-K по значимости, при равенстве — по идентификатору,
    // ровно как в оконной функции прежнего SQL.
    indices.sort((a, b) => c.significance[b]! - c.significance[a]! || c.id[a]! - c.id[b]!);

    const byKind: Record<string, number> = {};
    for (const [kind, n] of counts.get(bucket)!) byKind[meta!.kinds[kind] ?? 'other'] = n;

    const bound = bounds.get(bucket)!;

    items.push({
      index: bucket,
      start: from + Math.floor(bucket * width),
      end: from + Math.floor((bucket + 1) * width),
      tMin: bound.min,
      tMax: bound.max,
      total: totals.get(bucket)!,
      byKind,
      top: indices.slice(0, topk).map((i) => eventAt(i, lang)),
    });
  }

  return {
    from,
    to,
    buckets,
    bucketWidth: width,
    totalEvents: [...totals.values()].reduce((a, b) => a + b, 0),
    items,
  };
}

export async function fetchEventList(
  from: number,
  to: number,
  offset: number,
  limit: number,
  lang: string,
  filters: { kinds?: string[]; categories?: string[] },
  signal: AbortSignal,
): Promise<EventListResponse> {
  await ensureLanguage(lang);
  if (signal.aborted) throw new DOMException('aborted', 'AbortError');

  const c = core!;
  const parsed = buildFilters(filters);
  const matches: number[] = [];

  // Границы включительные с обеих сторон: у кластера шириной в сутки
  // они совпадают.
  for (let i = lowerBound(from); i < c.count; i++) {
    if (c.tMid[i]! > to) break;
    if (passes(i, parsed)) matches.push(i);
  }

  matches.sort((a, b) => c.significance[b]! - c.significance[a]! || c.id[a]! - c.id[b]!);

  return {
    total: matches.length,
    items: matches.slice(offset, offset + limit).map((i) => eventAt(i, lang)),
  };
}

// ---------------------------------------------------------------------
// Поиск
// ---------------------------------------------------------------------

/**
 * Поиск подстрокой по заголовкам и описаниям на языке интерфейса
 * и на английском.
 *
 * Морфологии здесь нет — она осталась в PostgreSQL вместе с сервером.
 * Взамен поиск мгновенный и работает без сети; для запроса из одного-двух
 * слов по двадцати пяти тысячам заголовков разница почти не ощущается.
 */
export async function search(query: string, lang: string, signal: AbortSignal): Promise<TimelineEvent[]> {
  await ensureLanguage(lang);
  if (signal.aborted) throw new DOMException('aborted', 'AbortError');

  const needle = query.trim().toLowerCase();
  if (needle.length < 2) return [];

  const c = core!;
  const primary = texts.get(lang);
  const fallback = texts.get(FALLBACK_LANG);
  const scored: { index: number; score: number }[] = [];

  for (let i = 0; i < c.count; i++) {
    const title = (primary?.title[i] || fallback?.title[i] || '').toLowerCase();
    let score = 0;

    if (title === needle) score = 4;
    else if (title.startsWith(needle)) score = 3;
    else if (title.includes(needle)) score = 2;
    else {
      const other = (fallback?.title[i] || '').toLowerCase();
      if (other !== title && other.includes(needle)) score = 2;
      else {
        const summary = (primary?.summary[i] || fallback?.summary[i] || '').toLowerCase();
        if (summary.includes(needle)) score = 1;
      }
    }

    if (score > 0) scored.push({ index: i, score });
  }

  scored.sort((a, b) => b.score - a.score || c.significance[b.index]! - c.significance[a.index]!);

  return scored.slice(0, 15).map((s) => eventAt(s.index, lang));
}
