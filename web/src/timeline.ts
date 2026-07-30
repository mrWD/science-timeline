import {
  fetchTimeline,
  type Meta,
  type TimelineBucket,
  type TimelineEvent,
  type TimelineResponse,
} from './api';
import { currentLanguage, formatRange, formatTick, t } from './i18n';
import {
  PRECISION_RANK,
  UNIT_RANK,
  generateTicks,
  zoomUnit,
  type Tick,
} from './timeAxis';

/** Куда от линии уходит событие. Раскладку можно менять на лету. */
export type Side = 'above' | 'below';

/**
 * Над линией — новое знание, под линией — изменения и применение.
 * Это лишь значения по умолчанию: жёсткое деление быстро надоедает,
 * поэтому каждый тип можно перекинуть на другую сторону в настройках.
 */
export const DEFAULT_SIDES: Record<string, Side> = {
  discovery: 'above',
  observation: 'above',
  confirmation: 'above',
  publication: 'above',
  invention: 'below',
  refutation: 'below',
  award: 'below',
  other: 'below',
};

/** Цвет по типу события — альтернатива раскраске по области науки. */
export const KIND_COLORS: Record<string, string> = {
  discovery: '#4C8DFF',
  observation: '#00A5B5',
  confirmation: '#22B07D',
  publication: '#8E6BF0',
  invention: '#DA6C2E',
  refutation: '#E5484D',
  award: '#F0A020',
  other: '#7A8794',
};

interface Marker {
  x: number;
  y: number;
  r: number;
  color: string;
  event?: TimelineEvent;
  bucket?: TimelineBucket;
}

interface Bar {
  x0: number;
  x1: number;
  y: number;
  h: number;
  color: string;
  event: TimelineEvent;
}

export interface TimelineCallbacks {
  onHover(payload: { event?: TimelineEvent; bucket?: TimelineBucket; x: number; y: number } | null): void;
  onRangeChange(label: string, total: number): void;
  onStatus(status: 'loading' | 'ready' | 'error', message?: string): void;
  /** Кластер, который приближением уже не разложить — его показывают списком. */
  onOpenList(bucket: TimelineBucket): void;
}

/**
 * Кластер, который приближением уже не разложить.
 *
 * У события с точностью до дня нет внутридневного времени: все статьи,
 * вышедшие 28 июля, стоят ровно в одной точке оси. Если все события бакета
 * пришлись на одну точку, кластер останется одним кружком на любом масштабе,
 * и клик по нему выглядит как «ничего не произошло». Единственный честный
 * выход — показать список.
 */
export const isAtomicCluster = (bucket: TimelineBucket): boolean =>
  bucket.tMax === bucket.tMin;

const MIN_SPAN_DAYS = 3;
const BAND_HEIGHT = 54;

/**
 * Какую долю экрана можно увести за край данных.
 *
 * Именно доля, а не абсолютный срок. Абсолютный не работает ни в одну
 * сторону: 10 лет на масштабе всей истории — меньше пикселя, и крайние
 * события намертво прилипают к рамке; а полтора процента от всего диапазона
 * — это полтора века пустоты на масштабе десятилетий. Доля от ширины окна
 * ведёт себя одинаково на любом масштабе: за краем данных всегда остаётся
 * пятая часть экрана, не больше и не меньше.
 */
const OVERSCROLL = 0.2;

/** Поля вокруг данных в режиме «вся история» — чтобы крайние точки не липли к рамке. */
const RESET_MARGIN = 0.03;

/**
 * Полоса под подписями шкалы. Ни точки, ни кластеры сюда не заходят —
 * иначе кружок с числом садится прямо на слово «июль» и не читается
 * ни то, ни другое.
 */
const LABEL_LANE = 22;

/** Все значения точности из БД, от самой грубой к самой мелкой. */
const ALL_PRECISIONS = ['unknown', 'millennium', 'century', 'decade', 'year', 'month', 'day'];

/** Сегодняшний день в координатах оси — сутки от 1970-01-01. */
export const today = (): number => Math.floor(Date.now() / 86_400_000);

export class Timeline {
  private readonly ctx: CanvasRenderingContext2D;
  private readonly resizeObserver: ResizeObserver;

  private lo = 0;
  private hi = 1;
  private width = 0;
  private height = 0;

  private data: TimelineResponse | null = null;
  private bandData: TimelineResponse | null = null;
  private markers: Marker[] = [];
  private bars: Bar[] = [];
  private hiddenInBand = 0;

  private inFlight: AbortController | null = null;
  private fetchTimer: number | null = null;
  private hovered: Marker | Bar | null = null;

  private colorByKind = false;
  private sides: Record<string, Side> = { ...DEFAULT_SIDES };
  private activeCategories = new Set<string>();
  private activeKinds = new Set<string>();

  private dragging = false;
  private dragStartX = 0;
  private dragStartLo = 0;
  private dragStartHi = 0;
  private dragMoved = false;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly meta: Meta,
    private readonly callbacks: TimelineCallbacks,
  ) {
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('canvas 2d недоступен');
    this.ctx = ctx;

    // Стартовый вид — текущая неделя.
    const now = today();
    this.lo = now - 3.5;
    this.hi = now + 3.5;

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);

    canvas.addEventListener('wheel', this.onWheel, { passive: false });
    canvas.addEventListener('pointerdown', this.onPointerDown);
    canvas.addEventListener('pointermove', this.onPointerMove);
    canvas.addEventListener('pointerup', this.onPointerUp);
    canvas.addEventListener('pointerleave', this.onPointerLeave);
    canvas.addEventListener('dblclick', this.onDoubleClick);

    this.resize();
  }

  destroy(): void {
    this.resizeObserver.disconnect();
    this.inFlight?.abort();
    if (this.fetchTimer !== null) window.clearTimeout(this.fetchTimer);
  }

  // ---------------------------------------------------------------
  // Настройки
  // ---------------------------------------------------------------

  setColorByKind(value: boolean): void {
    this.colorByKind = value;
    this.render();
  }

  setSide(kind: string, side: Side): void {
    this.sides[kind] = side;
    this.render();
  }

  getSide(kind: string): Side {
    return this.sides[kind] ?? 'below';
  }

  setCategoryFilter(slugs: Set<string>): void {
    this.activeCategories = slugs;
    this.scheduleFetch(0);
  }

  setKindFilter(kinds: Set<string>): void {
    this.activeKinds = kinds;
    this.scheduleFetch(0);
  }

  /**
   * Перезапросить данные и перерисовать — нужно после смены языка:
   * названия событий приходят с сервера, и одной перерисовки мало.
   */
  refresh(): void {
    this.callbacks.onRangeChange(formatRange(this.lo, this.hi), this.data?.totalEvents ?? 0);
    this.render();
    this.scheduleFetch(0);
  }

  /** Показать конкретный интервал — используется поиском и кликом по кластеру. */
  focus(start: number, end: number, padding = 0.25): void {
    const span = Math.max(end - start, MIN_SPAN_DAYS);
    const pad = span * padding;
    this.setRange(start - pad, end + pad);
  }

  zoomBy(factor: number): void {
    const centre = (this.lo + this.hi) / 2;
    const half = ((this.hi - this.lo) * factor) / 2;
    this.setRange(centre - half, centre + half);
  }

  /** Показать всю историю целиком, с небольшими полями по краям. */
  resetView(): void {
    const { min, max } = this.dataBounds();
    const margin = (max - min) * RESET_MARGIN;
    this.setRange(min - margin, max + margin);
  }

  /** Вернуться к текущей неделе. */
  goToToday(): void {
    const now = today();
    this.setRange(now - 3.5, now + 3.5);
  }

  /**
   * Границы самих данных, без всякого запаса.
   *
   * Правая граница считается по сегодняшнему дню, а не только по данным:
   * самое свежее событие может быть недельной давности, но смотреть
   * на текущую неделю всё равно нужно.
   */
  private dataBounds(): { min: number; max: number } {
    return { min: this.meta.minTime, max: Math.max(this.meta.maxTime, today()) };
  }

  // ---------------------------------------------------------------
  // Диапазон и загрузка
  // ---------------------------------------------------------------

  private setRange(lo: number, hi: number): void {
    const clamped = this.clamp(lo, hi);
    this.lo = clamped.lo;
    this.hi = clamped.hi;

    // Именно this.lo/this.hi, а не аргументы: после ограничения они другие,
    // и заголовок показывал бы диапазон, которого на экране нет.
    this.callbacks.onRangeChange(formatRange(this.lo, this.hi), this.data?.totalEvents ?? 0);
    this.render();
    this.scheduleFetch(120);
  }

  /**
   * Приводит запрошенный диапазон к допустимому: сначала ограничивает
   * ширину окна, затем задвигает его внутрь границ данных.
   *
   * Порядок важен. Если сдвигать раньше, чем ограничивать ширину,
   * окно шире всей истории оттолкнётся от левой границы и уедет вправо
   * вместо того, чтобы просто показать всё целиком.
   *
   * Запас за краем данных считается от ширины окна, а не от всего
   * диапазона. Это принципиально: история занимает почти десять тысяч лет,
   * и «полтора процента запаса» означали бы полтора века пустого поля —
   * формально в границах, а на деле та же бесконечная прокрутка в никуда.
   * От ширины окна запас честнее: крайнее событие доезжает ровно
   * до середины экрана и дальше не пускает.
   */
  private clamp(lo: number, hi: number): { lo: number; hi: number } {
    const { min: dataMin, max: dataMax } = this.dataBounds();

    let span = hi - lo;
    const maxSpan = (dataMax - dataMin) * (1 + 4 * RESET_MARGIN);
    if (span < MIN_SPAN_DAYS) span = MIN_SPAN_DAYS;
    if (span > maxSpan) span = maxSpan;

    const centre = (lo + hi) / 2;
    lo = centre - span / 2;
    hi = lo + span;

    const pad = span * OVERSCROLL;

    if (lo < dataMin - pad) { lo = dataMin - pad; hi = lo + span; }
    if (hi > dataMax + pad) { hi = dataMax + pad; lo = hi - span; }

    return { lo, hi };
  }

  private scheduleFetch(delay: number): void {
    if (this.fetchTimer !== null) window.clearTimeout(this.fetchTimer);
    this.fetchTimer = window.setTimeout(() => void this.load(), delay);
  }

  /**
   * Загрузка идёт двумя запросами: отдельно события, чья датировка не грубее
   * текущего масштаба, и отдельно все остальные.
   *
   * Разделять их на клиенте нельзя. События с точностью до года лежат на оси
   * в середине года, то есть все до единого попадают в один и тот же бакет.
   * При кластеризации они схлопываются в один серый кружок ещё до того, как
   * дело дойдёт до проверки точности, — и полоса «дата известна неточнее»
   * остаётся пустой. Поэтому разделение делает сам запрос, фильтром по точности.
   */
  private async load(): Promise<void> {
    this.inFlight?.abort();
    const controller = new AbortController();
    this.inFlight = controller;

    // Бакет шириной около 14 пикселей: точки не наезжают друг на друга,
    // но и разрешение не теряется.
    const buckets = Math.max(1, Math.min(2000, Math.round(this.width / 14)));
    const unitRank = UNIT_RANK[zoomUnit(this.hi - this.lo)];

    const precise = ALL_PRECISIONS.filter((p) => (PRECISION_RANK[p] ?? 0) >= unitRank);
    const imprecise = ALL_PRECISIONS.filter((p) => (PRECISION_RANK[p] ?? 0) < unitRank);

    const common = {
      from: this.lo,
      to: this.hi,
      categories: [...this.activeCategories],
      kinds: [...this.activeKinds],
      lang: currentLanguage(),
    };

    this.callbacks.onStatus('loading');

    try {
      const [data, band] = await Promise.all([
        fetchTimeline({ ...common, buckets, topk: 6, precisions: precise }, controller.signal),
        imprecise.length > 0
          ? fetchTimeline(
              // В полосе разрешение не нужно: там всё равно рисуются отрезки
              // во всю ширину интервала неопределённости.
              { ...common, buckets: Math.max(1, Math.round(this.width / 60)), topk: 3, precisions: imprecise },
              controller.signal,
            )
          : Promise.resolve(null),
      ]);

      this.data = data;
      this.bandData = band;

      this.callbacks.onRangeChange(
        formatRange(this.lo, this.hi),
        data.totalEvents + (band?.totalEvents ?? 0),
      );
      this.callbacks.onStatus('ready');
      this.render();
    } catch (error) {
      if ((error as Error).name === 'AbortError') return;
      this.callbacks.onStatus('error', (error as Error).message);
    }
  }

  // ---------------------------------------------------------------
  // Геометрия
  // ---------------------------------------------------------------

  private timeToX(time: number): number {
    return ((time - this.lo) / (this.hi - this.lo)) * this.width;
  }

  private xToTime(x: number): number {
    return this.lo + (x / this.width) * (this.hi - this.lo);
  }

  /**
   * Линия смещена вверх на половину полосы подписей: она съедает место
   * только снизу, и без поправки нижняя сторона получилась бы уже верхней.
   */
  private get axisY(): number {
    return (this.height - BAND_HEIGHT - LABEL_LANE) / 2;
  }

  private resize(): void {
    if (this.syncCanvasSize()) this.scheduleFetch(150);
    this.render();
  }

  // ---------------------------------------------------------------
  // Отрисовка
  // ---------------------------------------------------------------

  private css(name: string, fallback: string): string {
    const value = getComputedStyle(this.canvas).getPropertyValue(name).trim();
    return value || fallback;
  }

  private colorFor(event: TimelineEvent): string {
    if (this.colorByKind) return KIND_COLORS[event.kind] ?? KIND_COLORS.other!;

    const slug = event.categories[0];
    if (!slug) return KIND_COLORS.other!;

    return this.meta.categories.find((c) => c.slug === slug)?.color ?? KIND_COLORS.other!;
  }

  /**
   * Приводит буфер холста в соответствие с его размером на странице.
   * Возвращает true, если что-то изменилось.
   *
   * Полагаться на один только ResizeObserver нельзя: он работает через
   * цикл отрисовки, а тот не крутится, пока вкладка не показана. Если
   * страница загрузилась в фоне, наблюдатель не сработает ни разу и холст
   * так и останется нулевого размера.
   */
  private syncCanvasSize(): boolean {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return false;

    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(rect.width * dpr);
    const h = Math.round(rect.height * dpr);

    if (this.canvas.width === w && this.canvas.height === h) return false;

    this.width = rect.width;
    this.height = rect.height;
    this.canvas.width = w;
    this.canvas.height = h;
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return true;
  }

  render(): void {
    const { ctx } = this;

    if (this.syncCanvasSize()) this.scheduleFetch(150);
    if (this.width === 0 || this.height === 0) return;

    ctx.clearRect(0, 0, this.width, this.height);

    this.markers = [];
    this.bars = [];

    const ticks = generateTicks(this.lo, this.hi, Math.max(3, Math.round(this.width / 110)));

    this.drawBandBackground();
    this.drawAxis(ticks);

    if (this.data) {
      this.layoutAndDraw();
    }

    this.drawTickLabels(ticks);
    this.drawBandLabel();
    this.drawHoverHighlight();
  }

  private drawAxis(ticks: Tick[]): void {
    const { ctx } = this;
    const y = this.axisY;

    const axisColor = this.css('--axis', '#8892a4');
    const gridColor = this.css('--grid', 'rgba(136, 146, 164, 0.18)');

    ctx.save();
    for (const tick of ticks) {
      const x = Math.round(this.timeToX(tick.time)) + 0.5;

      ctx.strokeStyle = gridColor;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, this.height - BAND_HEIGHT);
      ctx.stroke();

      ctx.strokeStyle = axisColor;
      ctx.beginPath();
      ctx.moveTo(x, y - (tick.major ? 6 : 3));
      ctx.lineTo(x, y + (tick.major ? 6 : 3));
      ctx.stroke();
    }

    ctx.strokeStyle = axisColor;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(0, Math.round(y) + 0.5);
    ctx.lineTo(this.width, Math.round(y) + 0.5);
    ctx.stroke();
    ctx.restore();
  }

  /**
   * Подписи шкалы рисуются последними и с обводкой цветом фона.
   * Полоса под них зарезервирована, но поводки точек всё равно её пересекают,
   * а на плотных участках соседние подписи сходятся вплотную — обводка
   * оставляет текст читаемым в обоих случаях.
   *
   * Подпись пропускается, если налезает на предыдущую: лучше показать
   * половину засечек, чем нечитаемую кашу.
   */
  private drawTickLabels(ticks: Tick[]): void {
    const { ctx } = this;
    const y = this.axisY + 8;

    ctx.save();
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 3.5;

    let occupiedUntil = -Infinity;

    for (const tick of ticks) {
      ctx.font = tick.major
        ? '600 12px ui-sans-serif, system-ui, sans-serif'
        : '12px ui-sans-serif, system-ui, sans-serif';

      const label = formatTick(tick);
      const x = this.timeToX(tick.time);
      const half = ctx.measureText(label).width / 2;
      if (x - half < occupiedUntil + 6) continue;
      occupiedUntil = x + half;

      ctx.strokeStyle = this.css('--bg', '#0f1219');
      ctx.strokeText(label, x, y);
      ctx.fillStyle = this.css('--text-dim', '#8892a4');
      ctx.fillText(label, x, y);
    }

    ctx.restore();
  }

  private drawBandBackground(): void {
    const { ctx } = this;
    const top = this.height - BAND_HEIGHT;

    ctx.save();
    ctx.fillStyle = this.css('--band', 'rgba(136, 146, 164, 0.07)');
    ctx.fillRect(0, top, this.width, BAND_HEIGHT);

    ctx.strokeStyle = this.css('--grid', 'rgba(136, 146, 164, 0.18)');
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, top + 0.5);
    ctx.lineTo(this.width, top + 0.5);
    ctx.stroke();
    ctx.restore();
  }

  private drawBandLabel(): void {
    const { ctx } = this;
    const top = this.height - BAND_HEIGHT;
    const shown = this.bars.length;

    const dict = t();
    let text = dict.bandLabel;
    if (shown === 0) text += ` — ${dict.bandEmpty}`;
    else if (this.hiddenInBand > 0) text += ` — ${dict.bandMore(shown, this.hiddenInBand)}`;

    ctx.save();
    ctx.fillStyle = this.css('--text-dim', '#8892a4');
    ctx.font = '11px ui-sans-serif, system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.fillText(text, 10, top + 7);
    ctx.restore();
  }

  /**
   * Раскладка точек и их отрисовка.
   *
   * Событие попадает в нижнюю полосу, если его датировка грубее текущего
   * масштаба: показывать «1905 год» точкой на 3 марта было бы враньём.
   * Там оно рисуется отрезком во всю ширину своего интервала неопределённости.
   */
  private layoutAndDraw(): void {
    const data = this.data!;

    const above: Marker[] = [];
    const below: Marker[] = [];

    this.collectBand();

    for (const bucket of data.items) {
      const clustered = bucket.total > bucket.top.length;

      if (clustered) {
        const x = this.timeToX((bucket.start + bucket.end) / 2);
        if (x < -40 || x > this.width + 40) continue;

        // Кластер уходит на ту сторону, где событий больше.
        let aboveCount = 0;
        for (const [kind, n] of Object.entries(bucket.byKind)) {
          if (this.getSide(kind) === 'above') aboveCount += n;
        }

        const target = aboveCount * 2 >= bucket.total ? above : below;
        target.push({
          x,
          y: 0,
          r: Math.min(19, 7 + Math.log2(bucket.total) * 2.1),
          color: this.css('--cluster', '#6b7688'),
          bucket,
        });
        continue;
      }

      for (const event of bucket.top) {
        const x = this.timeToX(event.tMid);
        if (x < -20 || x > this.width + 20) continue;

        const target = this.getSide(event.kind) === 'above' ? above : below;
        target.push({ x, y: 0, r: 5, color: this.colorFor(event), event });
      }
    }

    this.stack(above, 'above');
    this.stack(below, 'below');

    for (const marker of [...above, ...below]) this.drawMarker(marker);
    for (const bar of this.bars) this.drawBar(bar);

    this.markers = [...above, ...below];
  }

  /**
   * Расставляет точки по рядам так, чтобы они не наезжали друг на друга:
   * точка садится в первый ряд, где справа хватает места.
   *
   * Высота ряда не фиксирована, а считается по самому крупному элементу
   * в нём. Кластер из сотни событий имеет радиус 19 против пяти у обычной
   * точки, и при постоянном шаге он вылезал бы в соседние ряды.
   */
  private stack(markers: Marker[], side: Side): void {
    markers.sort((a, b) => a.x - b.x);

    const rowRight: number[] = [];
    const rowRadius: number[] = [];
    const rows: Marker[][] = [];

    // Сколько места есть до края холста. Сверху мешает только верхняя
    // граница, снизу — полоса неточных дат.
    const available = side === 'above'
      ? this.axisY - 10
      : this.height - BAND_HEIGHT - this.axisY - LABEL_LANE - 10;

    for (const marker of markers) {
      const left = marker.x - marker.r;
      let row = rowRight.findIndex((right) => right + 3 <= left);

      if (row === -1) {
        // Заводим новый ряд, пока хватает высоты. Оценка грубая, по среднему
        // ряду в 15 пикселей: точный расчёт невозможен, высота ряда ещё
        // не известна — она зависит от того, что в него попадёт.
        if ((rowRight.length + 1) * 15 <= available) {
          row = rowRight.length;
          rowRight.push(0);
          rowRadius.push(0);
          rows.push([]);
        } else {
          // Рядов не хватило — кладём в последний. Лёгкое перекрытие
          // заметно меньше, чем пропавшее событие.
          row = rowRight.length - 1;
        }
      }

      if (row < 0) { row = 0; rowRight.push(0); rowRadius.push(0); rows.push([]); }

      rowRight[row] = marker.x + marker.r;
      rowRadius[row] = Math.max(rowRadius[row] ?? 0, marker.r);
      rows[row]!.push(marker);
    }

    // Ряды выкладываются от линии наружу, каждый со своей высотой.
    const direction = side === 'above' ? -1 : 1;
    let offset = side === 'above' ? 8 : LABEL_LANE + 4;

    for (let row = 0; row < rows.length; row++) {
      const radius = rowRadius[row] ?? 5;
      offset += radius;

      for (const marker of rows[row]!) marker.y = this.axisY + direction * offset;

      offset += radius + 4;
    }
  }

  /**
   * Собирает нижнюю полосу: события, чья датировка грубее текущего масштаба.
   *
   * Рисуются отрезком во всю ширину интервала неопределённости, а не точкой:
   * «1905 год» на дневной шкале — это весь год, и притворяться, что известен
   * конкретный день, нельзя. Ровно поэтому такие события и не привязываются
   * к 1 января.
   */
  private collectBand(): void {
    this.hiddenInBand = 0;
    if (!this.bandData) return;

    for (const bucket of this.bandData.items) {
      this.hiddenInBand += bucket.total - bucket.top.length;

      for (const event of bucket.top) {
        const x0 = this.timeToX(event.tStart);
        const x1 = this.timeToX(event.tEnd);
        if (x1 < -40 || x0 > this.width + 40) continue;

        this.bars.push({ x0, x1, y: 0, h: 7, color: this.colorFor(event), event });
      }
    }

    this.bars.sort((a, b) => a.x0 - b.x0);

    const rowRight: number[] = [];
    const top = this.height - BAND_HEIGHT + 24;
    const rowHeight = 10;
    const maxRows = 3;

    for (const bar of this.bars) {
      let row = rowRight.findIndex((right) => right + 4 <= bar.x0);

      if (row === -1) {
        if (rowRight.length < maxRows) {
          row = rowRight.length;
          rowRight.push(0);
        } else {
          // Рядов не хватило. Отрезок всё равно рисуем — при полном
          // перекрытии он остаётся доступен по наведению.
          row = maxRows - 1;
        }
      }

      rowRight[row] = bar.x1;
      bar.y = top + row * rowHeight;
    }
  }

  private drawMarker(marker: Marker): void {
    const { ctx } = this;

    if (marker.bucket) {
      ctx.save();
      ctx.beginPath();
      ctx.arc(marker.x, marker.y, marker.r, 0, Math.PI * 2);
      ctx.fillStyle = this.css('--cluster-fill', 'rgba(107, 118, 136, 0.28)');
      ctx.fill();
      ctx.strokeStyle = marker.color;
      ctx.lineWidth = 1.5;
      ctx.stroke();

      ctx.fillStyle = this.css('--text', '#e6e9ef');
      ctx.font = '600 11px ui-sans-serif, system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(marker.bucket.total), marker.x, marker.y);
      ctx.restore();
      return;
    }

    const event = marker.event!;
    ctx.save();

    // Тонкий поводок к линии — иначе при высоком стеке непонятно,
    // к какому месту оси относится точка. Снизу поводок останавливается
    // у полосы подписей, чтобы не перечёркивать их.
    const anchor = marker.y < this.axisY ? this.axisY : this.axisY + LABEL_LANE;
    ctx.strokeStyle = this.css('--grid', 'rgba(136, 146, 164, 0.18)');
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(marker.x, marker.y);
    ctx.lineTo(marker.x, anchor);
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(marker.x, marker.y, marker.r, 0, Math.PI * 2);
    ctx.fillStyle = marker.color;
    ctx.fill();

    // Премии и опровержения обводим — их просили выделять отдельно,
    // и обводка читается даже когда цвет занят областью науки.
    if (event.kind === 'award' || event.kind === 'refutation') {
      ctx.strokeStyle = this.css('--bg', '#12151c');
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.strokeStyle = event.kind === 'award' ? KIND_COLORS.award! : KIND_COLORS.refutation!;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(marker.x, marker.y, marker.r + 2.5, 0, Math.PI * 2);
      ctx.stroke();
    }

    ctx.restore();
  }

  private drawBar(bar: Bar): void {
    const { ctx } = this;
    const x0 = Math.max(-2, bar.x0);
    const x1 = Math.min(this.width + 2, bar.x1);

    ctx.save();
    ctx.fillStyle = bar.color;
    ctx.globalAlpha = 0.75;
    ctx.beginPath();
    ctx.roundRect(x0, bar.y, Math.max(3, x1 - x0), bar.h, bar.h / 2);
    ctx.fill();
    ctx.restore();
  }

  private drawHoverHighlight(): void {
    if (!this.hovered) return;
    const { ctx } = this;

    ctx.save();
    ctx.strokeStyle = this.css('--text', '#e6e9ef');
    ctx.lineWidth = 2;

    if ('r' in this.hovered) {
      ctx.beginPath();
      ctx.arc(this.hovered.x, this.hovered.y, this.hovered.r + 3.5, 0, Math.PI * 2);
      ctx.stroke();
    } else {
      const bar = this.hovered;
      ctx.beginPath();
      ctx.roundRect(bar.x0 - 1.5, bar.y - 1.5, Math.max(3, bar.x1 - bar.x0) + 3, bar.h + 3, (bar.h + 3) / 2);
      ctx.stroke();
    }
    ctx.restore();
  }

  // ---------------------------------------------------------------
  // Взаимодействие
  // ---------------------------------------------------------------

  private hitTest(x: number, y: number): Marker | Bar | null {
    let best: Marker | null = null;
    let bestDistance = Infinity;

    for (const marker of this.markers) {
      const dx = marker.x - x;
      const dy = marker.y - y;
      const distance = Math.hypot(dx, dy);

      if (distance <= marker.r + 4 && distance < bestDistance) {
        best = marker;
        bestDistance = distance;
      }
    }
    if (best) return best;

    for (const bar of this.bars) {
      if (x >= bar.x0 - 3 && x <= bar.x1 + 3 && y >= bar.y - 3 && y <= bar.y + bar.h + 3) return bar;
    }
    return null;
  }

  private onWheel = (event: WheelEvent): void => {
    event.preventDefault();

    const rect = this.canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const anchor = this.xToTime(x);

    // Зум относительно курсора: точка под указателем остаётся на месте,
    // как в картах. Ctrl+колесо на тачпадах даёт жест «щипок».
    const intensity = event.ctrlKey ? 0.012 : 0.0022;
    const factor = Math.exp(event.deltaY * intensity);

    const lo = anchor - (anchor - this.lo) * factor;
    const hi = anchor + (this.hi - anchor) * factor;
    this.setRange(lo, hi);
  };

  private onPointerDown = (event: PointerEvent): void => {
    this.dragging = true;
    this.dragMoved = false;
    this.dragStartX = event.clientX;
    this.dragStartLo = this.lo;
    this.dragStartHi = this.hi;
    this.canvas.setPointerCapture(event.pointerId);
    this.canvas.style.cursor = 'grabbing';
  };

  private onPointerMove = (event: PointerEvent): void => {
    const rect = this.canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;

    if (this.dragging) {
      const dx = event.clientX - this.dragStartX;
      if (Math.abs(dx) > 3) this.dragMoved = true;

      const span = this.dragStartHi - this.dragStartLo;
      const shift = (dx / this.width) * span;

      // Через clamp, иначе перетаскиванием можно уехать за пределы данных
      // в бесконечную пустоту.
      const clamped = this.clamp(this.dragStartLo - shift, this.dragStartHi - shift);
      this.lo = clamped.lo;
      this.hi = clamped.hi;

      this.callbacks.onRangeChange(formatRange(this.lo, this.hi), this.data?.totalEvents ?? 0);
      this.render();
      this.scheduleFetch(160);
      return;
    }

    const hit = this.hitTest(x, y);
    if (hit !== this.hovered) {
      this.hovered = hit;
      this.canvas.style.cursor = hit ? 'pointer' : 'grab';
      this.render();

      if (!hit) {
        this.callbacks.onHover(null);
      } else if ('r' in hit && hit.bucket) {
        this.callbacks.onHover({ bucket: hit.bucket, x: event.clientX, y: event.clientY });
      } else {
        const target = 'event' in hit ? hit.event : undefined;
        this.callbacks.onHover({ event: target, x: event.clientX, y: event.clientY });
      }
    }
  };

  private onPointerUp = (event: PointerEvent): void => {
    this.dragging = false;
    this.canvas.releasePointerCapture(event.pointerId);
    this.canvas.style.cursor = this.hovered ? 'pointer' : 'grab';

    if (this.dragMoved) {
      this.scheduleFetch(0);
      return;
    }

    const hit = this.hovered;
    if (hit && 'r' in hit && hit.bucket) {
      // Клик по кластеру приближает его интервал — но только если там есть
      // что приближать. Кластер шириной в сутки распасться уже не может.
      if (isAtomicCluster(hit.bucket)) this.callbacks.onOpenList(hit.bucket);
      else this.focus(hit.bucket.start, hit.bucket.end);
    }
  };

  private onPointerLeave = (): void => {
    this.hovered = null;
    this.callbacks.onHover(null);
    this.render();
  };

  private onDoubleClick = (event: MouseEvent): void => {
    const rect = this.canvas.getBoundingClientRect();
    const anchor = this.xToTime(event.clientX - rect.left);
    const factor = 0.4;

    this.setRange(anchor - (anchor - this.lo) * factor, anchor + (this.hi - anchor) * factor);
  };
}
