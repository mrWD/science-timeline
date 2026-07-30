import './style.css';
import {
  fetchEventList,
  fetchMeta,
  search,
  type Meta,
  type TimelineBucket,
  type TimelineEvent,
} from './dataset';
import {
  LANGUAGES,
  currentLanguage,
  detectLanguage,
  formatDate,
  setLanguage,
  t,
  type LangCode,
} from './i18n';
import { KIND_COLORS, Timeline, isAtomicCluster, type Side } from './timeline';

const $ = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (!element) throw new Error(`нет элемента #${id}`);
  return element as T;
};

const canvas = $<HTMLCanvasElement>('timeline');
const card = $<HTMLDivElement>('card');
const statusEl = $<HTMLSpanElement>('status');
const rangeLabelEl = $<HTMLSpanElement>('range-label');
const rangeCountEl = $<HTMLSpanElement>('range-count');
const searchInput = $<HTMLInputElement>('search-input');
const searchResults = $<HTMLUListElement>('search-results');
const themeSelect = $<HTMLSelectElement>('theme-select');
const langSelect = $<HTMLSelectElement>('lang-select');

let timeline: Timeline;
let meta: Meta;

const activeCategories = new Set<string>();
const activeKinds = new Set<string>();

// ---------------------------------------------------------------------
// Тема
// ---------------------------------------------------------------------

type Theme = 'auto' | 'light' | 'dark';

function applyTheme(theme: Theme): void {
  // В режиме «как в системе» атрибут снимается совсем, и решение
  // остаётся за медиазапросом prefers-color-scheme.
  if (theme === 'auto') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', theme);

  localStorage.setItem('theme', theme);
  timeline?.render();
}

function setupTheme(): void {
  const saved = (localStorage.getItem('theme') as Theme | null) ?? 'auto';
  themeSelect.value = saved;
  applyTheme(saved);

  themeSelect.addEventListener('change', () => applyTheme(themeSelect.value as Theme));

  // Холст читает цвета из CSS-переменных, поэтому смену системной темы
  // надо не только пропустить в CSS, но и перерисовать.
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((localStorage.getItem('theme') ?? 'auto') === 'auto') timeline?.render();
  });
}

// ---------------------------------------------------------------------
// Язык
// ---------------------------------------------------------------------

/** Проставляет переводы во всю разметку, помеченную data-i18n. */
function applyTranslations(): void {
  const dict = t();
  const lookup = dict as unknown as Record<string, string>;

  for (const element of document.querySelectorAll<HTMLElement>('[data-i18n]')) {
    const value = lookup[element.dataset.i18n!];
    if (typeof value === 'string') element.textContent = value;
  }

  for (const element of document.querySelectorAll<HTMLElement>('[data-i18n-title]')) {
    const value = lookup[element.dataset.i18nTitle!];
    if (typeof value === 'string') element.title = value;
  }

  document.title = dict.appTitle;
  searchInput.placeholder = dict.searchPlaceholder;
}

function setupLanguage(): void {
  for (const { code, name } of LANGUAGES) {
    const option = document.createElement('option');
    option.value = code;
    option.textContent = name;
    langSelect.append(option);
  }

  const saved = localStorage.getItem('lang') as LangCode | null;
  const initial = saved ?? detectLanguage();

  langSelect.value = initial;
  setLanguage(initial);

  langSelect.addEventListener('change', () => {
    const lang = langSelect.value as LangCode;
    localStorage.setItem('lang', lang);
    setLanguage(lang);

    applyTranslations();
    rebuildPanel();
    card.hidden = true;
    closeList();
    timeline.refresh();
  });
}

// ---------------------------------------------------------------------
// Карточка события
// ---------------------------------------------------------------------

const categoryColor = (slug: string): string =>
  meta.categories.find((c) => c.slug === slug)?.color ?? '#7A8794';

const categoryName = (slug: string): string => t().categories[slug] ?? slug;

const kindName = (kind: string): string => t().kinds[kind] ?? kind;

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]!,
  );
}

function renderEventCard(event: TimelineEvent): string {
  const tags = event.categories
    .map(
      (slug) =>
        `<span class="c-tag" style="color:${categoryColor(slug)}">${escapeHtml(categoryName(slug))}</span>`,
    )
    .join('');

  const kindTag = `<span class="c-tag" style="color:${KIND_COLORS[event.kind] ?? '#7A8794'}">${
    escapeHtml(kindName(event.kind))
  }</span>`;

  // Дата собирается на клиенте из интервала и точности — так подпись
  // получается на текущем языке без переимпорта данных.
  const date = formatDate(event.tStart, event.precision, event.circa);

  return `
    <h3>${escapeHtml(event.title)}</h3>
    <span class="c-date">${escapeHtml(date)}</span>
    ${event.summary ? `<p class="c-summary">${escapeHtml(event.summary)}</p>` : ''}
    ${renderImage(event.imageUrl)}
    <div class="c-tags">${kindTag}${tags}</div>
    ${event.url ? `<span class="c-link">${escapeHtml(event.url.replace(/^https?:\/\//, ''))}</span>` : ''}
  `;
}

/**
 * Картинка с указанием источника.
 *
 * Изображения приходят с Викисклада, где значительная часть файлов под
 * CC BY-SA — а она требует указания авторства. Имени автора в данных нет
 * (это отдельный запрос на каждый файл), поэтому подписывается название
 * файла и сам склад: по ним страница файла с полной лицензией находится
 * однозначно. Так же поступает и сама Википедия.
 */
function renderImage(url: string | null): string {
  if (!url) return '';

  const file = decodeURIComponent(url.split('/').pop() ?? '').replace(/_/g, ' ');
  const credit = file ? `${file} · Wikimedia Commons` : 'Wikimedia Commons';

  return `
    <img src="${escapeHtml(url)}" alt="" loading="lazy" />
    <span class="c-credit">${escapeHtml(credit)}</span>
  `;
}

function renderClusterCard(bucket: TimelineBucket): string {
  const dict = t();
  const rows = Object.entries(bucket.byKind)
    .sort((a, b) => b[1] - a[1])
    .map(
      ([kind, n]) =>
        `<li><span style="color:${KIND_COLORS[kind] ?? '#7A8794'}">${escapeHtml(
          kindName(kind),
        )}</span><span>${n}</span></li>`,
    )
    .join('');

  // Подсказка честная: если кластер шириной в сутки, приближать нечего,
  // и клик откроет список, а не изменит масштаб.
  const hint = isAtomicCluster(bucket) ? dict.clusterHintList : dict.clusterHint;

  return `
    <h3>${escapeHtml(dict.clusterTitle(bucket.total))}</h3>
    <span class="c-date">${escapeHtml(hint)}</span>
    <ul class="c-breakdown">${rows}</ul>
  `;
}

// ---------------------------------------------------------------------
// Список событий кластера
// ---------------------------------------------------------------------

const listPanel = $<HTMLElement>('event-list');
const listTitle = $<HTMLHeadingElement>('event-list-title');
const listDate = $<HTMLSpanElement>('event-list-date');
const listItems = $<HTMLUListElement>('event-list-items');
const listMore = $<HTMLButtonElement>('event-list-more');

const PAGE_SIZE = 25;

let listBucket: TimelineBucket | null = null;
let listOffset = 0;
let listTotal = 0;
let listController: AbortController | null = null;

function closeList(): void {
  listController?.abort();
  listPanel.hidden = true;
  listBucket = null;
  listItems.replaceChildren();
}

function renderListItem(event: TimelineEvent): HTMLLIElement {
  const item = document.createElement('li');

  const tags = event.categories
    .map((slug) => `<span style="color:${categoryColor(slug)}">${escapeHtml(categoryName(slug))}</span>`)
    .join('');

  const link = event.url
    ? `<a href="${escapeHtml(event.url)}" target="_blank" rel="noopener">${escapeHtml(
        event.url.replace(/^https?:\/\/(www\.)?/, '').slice(0, 34),
      )}</a>`
    : '';

  item.innerHTML = `
    <span class="l-title">${escapeHtml(event.title)}</span>
    ${event.summary ? `<span class="l-summary">${escapeHtml(event.summary.slice(0, 180))}</span>` : ''}
    <span class="l-meta">
      <span style="color:${KIND_COLORS[event.kind] ?? '#7A8794'}">${escapeHtml(kindName(event.kind))}</span>
      ${tags}${link}
    </span>
  `;

  return item;
}

async function loadListPage(): Promise<void> {
  if (!listBucket) return;

  listController?.abort();
  listController = new AbortController();

  try {
    const page = await fetchEventList(
      // Фактические границы данных, а не расчётные края бакета: последние
      // округляются до соседних суток и притянули бы чужие события.
      listBucket.tMin,
      listBucket.tMax,
      listOffset,
      PAGE_SIZE,
      currentLanguage(),
      { kinds: [...activeKinds], categories: [...activeCategories] },
      listController.signal,
    );

    listTotal = page.total;
    for (const event of page.items) listItems.append(renderListItem(event));

    listOffset += page.items.length;

    const dict = t();
    listTitle.textContent = dict.clusterTitle(listTotal);
    listDate.textContent = dict.listShowing(listOffset, listTotal);

    listMore.textContent = dict.listMore;
    listMore.hidden = listOffset >= listTotal;
  } catch (error) {
    if ((error as Error).name !== 'AbortError') console.error(error);
  }
}

function openList(bucket: TimelineBucket): void {
  listBucket = bucket;
  listOffset = 0;
  listTotal = bucket.total;
  listItems.replaceChildren();

  listTitle.textContent = t().clusterTitle(bucket.total);
  listDate.textContent = formatDate(bucket.tMin, 'day');
  listPanel.hidden = false;
  card.hidden = true;

  void loadListPage();
}

/** Карточка не должна вылезать за окно — иначе у краёв ленты её не прочитать. */
function positionCard(x: number, y: number): void {
  const rect = card.getBoundingClientRect();
  const pad = 14;

  let left = x + 18;
  if (left + rect.width > window.innerWidth - pad) left = x - rect.width - 18;
  if (left < pad) left = pad;

  let top = y + 18;
  if (top + rect.height > window.innerHeight - pad) top = y - rect.height - 18;
  if (top < pad) top = pad;

  card.style.left = `${left}px`;
  card.style.top = `${top}px`;
}

// ---------------------------------------------------------------------
// Панель фильтров
// ---------------------------------------------------------------------

/**
 * Панель собирается заново при смене языка. Выбранные фильтры живут
 * в множествах вне DOM, поэтому пересборка их не сбрасывает.
 */
function rebuildPanel(): void {
  buildChips($<HTMLDivElement>('category-filters'), meta.categories.map((c) => c.slug), activeCategories, {
    label: categoryName,
    color: (slug) => categoryColor(slug),
    onChange: () => timeline.setCategoryFilter(new Set(activeCategories)),
  });

  buildChips($<HTMLDivElement>('kind-filters'), meta.kinds, activeKinds, {
    label: kindName,
    color: (kind) => KIND_COLORS[kind] ?? '#7A8794',
    onChange: () => timeline.setKindFilter(new Set(activeKinds)),
  });

  buildSideControls();
}

interface ChipOptions {
  label(key: string): string;
  color(key: string): string;
  onChange(): void;
}

function buildChips(container: HTMLElement, keys: string[], active: Set<string>, options: ChipOptions): void {
  container.replaceChildren();

  for (const key of keys) {
    const chip = document.createElement('button');
    chip.className = active.has(key) ? 'chip on' : 'chip';
    chip.style.color = options.color(key);
    chip.innerHTML = `<span class="dot"></span>${escapeHtml(options.label(key))}`;

    chip.addEventListener('click', () => {
      if (active.has(key)) {
        active.delete(key);
        chip.classList.remove('on');
      } else {
        active.add(key);
        chip.classList.add('on');
      }
      options.onChange();
    });

    container.append(chip);
  }
}

function buildSideControls(): void {
  const container = $<HTMLDivElement>('side-controls');
  container.replaceChildren();

  for (const kind of meta.kinds) {
    const row = document.createElement('div');
    row.className = 'side-row';

    const label = document.createElement('span');
    label.textContent = kindName(kind);

    const button = document.createElement('button');
    const paint = (): void => {
      const side: Side = timeline.getSide(kind);
      button.textContent = side === 'above' ? t().above : t().below;
    };

    button.addEventListener('click', () => {
      timeline.setSide(kind, timeline.getSide(kind) === 'above' ? 'below' : 'above');
      paint();
    });

    paint();
    row.append(label, button);
    container.append(row);
  }
}

// ---------------------------------------------------------------------
// Поиск
// ---------------------------------------------------------------------

function setupSearch(): void {
  let controller: AbortController | null = null;
  let timer: number | null = null;

  const hide = (): void => {
    searchResults.hidden = true;
    searchResults.replaceChildren();
  };

  searchInput.addEventListener('input', () => {
    if (timer !== null) window.clearTimeout(timer);
    const query = searchInput.value.trim();

    if (query.length < 2) {
      hide();
      return;
    }

    timer = window.setTimeout(async () => {
      controller?.abort();
      controller = new AbortController();

      try {
        const results = await search(query, currentLanguage(), controller.signal);
        searchResults.replaceChildren();

        if (results.length === 0) {
          const empty = document.createElement('li');
          empty.textContent = t().nothingFound;
          searchResults.append(empty);
        }

        for (const event of results) {
          const item = document.createElement('li');
          item.innerHTML = `<span class="r-title">${escapeHtml(event.title)}</span><span class="r-date">${escapeHtml(
            formatDate(event.tStart, event.precision, event.circa),
          )}</span>`;

          item.addEventListener('click', () => {
            // Приближаем так, чтобы вокруг события осталось немного контекста.
            const span = Math.max(event.tEnd - event.tStart, 365);
            timeline.focus(event.tMid - span * 3, event.tMid + span * 3, 0);
            hide();
            searchInput.blur();
          });

          searchResults.append(item);
        }

        searchResults.hidden = false;
      } catch (error) {
        if ((error as Error).name !== 'AbortError') hide();
      }
    }, 220);
  });

  document.addEventListener('click', (event) => {
    if (!searchInput.contains(event.target as Node) && !searchResults.contains(event.target as Node)) hide();
  });
}

// ---------------------------------------------------------------------
// Запуск
// ---------------------------------------------------------------------

async function main(): Promise<void> {
  setupLanguage();
  applyTranslations();
  statusEl.textContent = t().loading;

  try {
    meta = await fetchMeta();
  } catch (error) {
    statusEl.className = 'status error';
    statusEl.textContent = t().apiUnavailable;
    console.error(error);
    return;
  }

  timeline = new Timeline(canvas, meta, {
    onHover(payload) {
      if (!payload) {
        card.hidden = true;
        return;
      }

      if (payload.bucket) {
        card.innerHTML = renderClusterCard(payload.bucket);
      } else if (payload.event) {
        card.innerHTML = renderEventCard(payload.event);
      } else {
        card.hidden = true;
        return;
      }

      card.hidden = false;
      positionCard(payload.x, payload.y);
    },

    onRangeChange(label, total) {
      rangeLabelEl.textContent = label;
      rangeCountEl.textContent = total > 0 ? t().eventsInView(total) : '';
    },

    onStatus(status, message) {
      statusEl.className = status === 'error' ? 'status error' : 'status';
      statusEl.textContent =
        status === 'loading' ? t().loading : status === 'error' ? (message ?? '') : '';
    },

    onOpenList: openList,
  });

  listMore.addEventListener('click', () => void loadListPage());
  $('event-list-close').addEventListener('click', closeList);

  setupTheme();
  rebuildPanel();
  setupSearch();

  const colorByKind = $<HTMLInputElement>('color-by-kind');
  colorByKind.addEventListener('change', () => timeline.setColorByKind(colorByKind.checked));

  $('zoom-in').addEventListener('click', () => timeline.zoomBy(0.55));
  $('zoom-out').addEventListener('click', () => timeline.zoomBy(1.8));
  $('go-today').addEventListener('click', () => timeline.goToToday());
  $('zoom-reset').addEventListener('click', () => timeline.resetView());

  window.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') closeList();
    if (event.target instanceof HTMLInputElement) return;

    if (event.key === '+' || event.key === '=') timeline.zoomBy(0.6);
    if (event.key === '-' || event.key === '_') timeline.zoomBy(1.7);
    if (event.key === '0') timeline.resetView();
    if (event.key.toLowerCase() === 't') timeline.goToToday();
  });

  // Клик по точке открывает статью — карточка показывается по наведению,
  // а переход по ссылке требует явного действия.
  canvas.addEventListener('click', () => {
    const link = card.querySelector('.c-link');
    if (!card.hidden && link) {
      const url = link.textContent;
      if (url) window.open(`https://${url}`, '_blank', 'noopener');
    }
  });

  timeline.goToToday();
}

void main();
