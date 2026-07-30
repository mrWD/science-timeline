/**
 * Ось времени — точная копия ScienceTimeline.Core/TimeAxis.cs.
 *
 * Единица — целые сутки от 1970-01-01, счёт ведётся через номер юлианского
 * дня, годы астрономические (1 г. до н. э. = 0). Обе реализации обязаны
 * давать одинаковые числа: сервер присылает точки уже в этих координатах,
 * и любое расхождение сдвинет всю ленту.
 *
 * Math.floor здесь везде неслучаен — он соответствует FloorDiv из C#,
 * тогда как обычное деление в JS для отрицательных годов округляло бы
 * в другую сторону.
 *
 * Модуль намеренно не знает ни одного слова ни на одном языке: засечки
 * он отдаёт числами, а подписи к ним собирает i18n. Иначе получился бы
 * цикл импорта, ведь i18n нужна отсюда функция toGregorian.
 */

export const UNIX_EPOCH_JDN = 2_440_588;

export function gregorianToJdn(year: number, month: number, day: number): number {
  const a = Math.floor((14 - month) / 12);
  const y = year + 4800 - a;
  const m = month + 12 * a - 3;

  return (
    day +
    Math.floor((153 * m + 2) / 5) +
    365 * y +
    Math.floor(y / 4) -
    Math.floor(y / 100) +
    Math.floor(y / 400) -
    32045
  );
}

export interface CalendarDate {
  year: number;
  month: number;
  day: number;
}

export function jdnToGregorian(jdn: number): CalendarDate {
  const a = jdn + 32044;
  const b = Math.floor((4 * a + 3) / 146097);
  const c = a - Math.floor((146097 * b) / 4);
  const d = Math.floor((4 * c + 3) / 1461);
  const e = c - Math.floor((1461 * d) / 4);
  const m = Math.floor((5 * e + 2) / 153);

  return {
    day: e - Math.floor((153 * m + 2) / 5) + 1,
    month: m + 3 - 12 * Math.floor(m / 10),
    year: 100 * b + d - 4800 + Math.floor(m / 10),
  };
}

export const fromGregorian = (year: number, month: number, day: number): number =>
  gregorianToJdn(year, month, day) - UNIX_EPOCH_JDN;

export const toGregorian = (dayNumber: number): CalendarDate =>
  jdnToGregorian(Math.floor(dayNumber) + UNIX_EPOCH_JDN);

export const startOfYear = (year: number): number => fromGregorian(year, 1, 1);

// ---------------------------------------------------------------------
// Засечки шкалы
// ---------------------------------------------------------------------

export type TickUnit = 'day' | 'month' | 'year' | 'decade' | 'century' | 'millennium';

export interface Tick extends CalendarDate {
  /** Позиция на оси, в сутках от эпохи. */
  time: number;
  /** Крупные засечки рисуются ярче и подписываются полнее. */
  major: boolean;
  /** Что именно подписывать: число месяца, месяц или год. */
  unit: 'day' | 'month' | 'year';
}

/**
 * Уровень детализации, которому соответствует текущий масштаб.
 * По нему же решается, у каких событий дата «слишком грубая»
 * и они уходят в отдельную полосу.
 */
export function zoomUnit(spanDays: number): TickUnit {
  if (spanDays < 60) return 'day';
  if (spanDays < 800) return 'month';
  if (spanDays < 8_000) return 'year';
  if (spanDays < 60_000) return 'decade';
  if (spanDays < 600_000) return 'century';
  return 'millennium';
}

/** Насколько точна датировка — больше значит точнее. Совпадает с enum в БД. */
export const PRECISION_RANK: Record<string, number> = {
  day: 6,
  month: 5,
  year: 4,
  decade: 3,
  century: 2,
  millennium: 1,
  unknown: 0,
};

export const UNIT_RANK: Record<TickUnit, number> = {
  day: 6,
  month: 5,
  year: 4,
  decade: 3,
  century: 2,
  millennium: 1,
};

const YEAR_STEPS = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];

/**
 * Засечки строятся по календарным границам, а не по кратным числам суток.
 * Иначе на годовом масштабе подписи разъезжаются: 365 суток — это не год,
 * и каждые четыре года засечка уползала бы на день.
 */
export function generateTicks(lo: number, hi: number, targetCount: number): Tick[] {
  const span = hi - lo;
  if (span <= 0 || targetCount <= 0) return [];

  const approxStep = span / targetCount;
  const ticks: Tick[] = [];

  // --- сутки -------------------------------------------------------
  if (approxStep < 25) {
    const step = [1, 2, 5, 10].find((s) => s >= approxStep) ?? 10;
    const first = Math.ceil(lo / step) * step;

    for (let t = first; t <= hi; t += step) {
      const date = toGregorian(t);
      ticks.push({ time: t, ...date, major: date.day === 1, unit: date.day === 1 ? 'month' : 'day' });
    }
    return ticks;
  }

  // --- месяцы ------------------------------------------------------
  if (approxStep < 320) {
    const step = [1, 3, 6].find((s) => s * 30.44 >= approxStep) ?? 6;
    const start = toGregorian(lo);
    let year = start.year;
    let month = Math.floor((start.month - 1) / step) * step + 1;

    for (;;) {
      const t = fromGregorian(year, month, 1);
      if (t > hi) break;
      if (t >= lo) {
        ticks.push({
          time: t,
          year,
          month,
          day: 1,
          major: month === 1,
          unit: month === 1 ? 'year' : 'month',
        });
      }

      month += step;
      while (month > 12) {
        month -= 12;
        year += 1;
      }
    }
    return ticks;
  }

  // --- годы и крупнее ----------------------------------------------
  const approxYears = approxStep / 365.2425;
  const step = YEAR_STEPS.find((s) => s >= approxYears) ?? YEAR_STEPS[YEAR_STEPS.length - 1]!;
  const startYear = toGregorian(lo).year;
  const endYear = toGregorian(hi).year;

  let year = Math.floor(startYear / step) * step;
  for (; year <= endYear; year += step) {
    const t = startOfYear(year);
    if (t < lo || t > hi) continue;

    // Каждая десятая засечка — крупная: она даёт глазу опору
    // при быстром зуме, когда подписи мелькают.
    ticks.push({ time: t, year, month: 1, day: 1, major: year % (step * 10) === 0, unit: 'year' });
  }
  return ticks;
}
