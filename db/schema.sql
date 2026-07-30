-- =====================================================================
-- science-timeline — схема БД (PostgreSQL 18)
--
-- Главное проектное решение: время хранится НЕ в типе date, а числом —
-- целые сутки от 1970-01-01 в пролептическом григорианском календаре.
-- Это даёт три вещи, которых с date не получить:
--   1. даты до н.э. работают без спецслучаев;
--   2. неточная дата («около 300 г. до н.э.») — это просто широкий
--      полуинтервал [t_start, t_end), а не выдуманное 1 января;
--   3. зум на любом масштабе — обычный range-скан по btree-индексу.
--
-- Интервал всегда полуоткрытый: [t_start, t_end).
--   точная дата  -> t_end = t_start + 1
--   «1905 год»   -> весь 1905-й
--   «XIX век»    -> весь век
-- =====================================================================

begin;

-- ---------------------------------------------------------------------
-- Перечисления
-- ---------------------------------------------------------------------

-- Тип события. Определяет, с какой стороны линии рисуется точка
-- и каким цветом. Раскладку по сторонам задаёт фронтенд, а не БД, —
-- в переписке ты просил возможность менять её самому.
create type event_kind as enum (
    'discovery',     -- открытие
    'invention',     -- изобретение
    'refutation',    -- опровержение
    'confirmation',  -- подтверждение
    'publication',   -- публикация
    'observation',   -- наблюдение
    'award',         -- премия
    'other'
);

-- Точность датировки. Хранится отдельно от интервала, потому что
-- «1 января 1905» и «где-то в 1905» дают разные интервалы, но ещё
-- важнее — по-разному показываются на дневном масштабе.
create type date_precision as enum (
    'day',
    'month',
    'year',
    'decade',
    'century',
    'millennium',
    'unknown'
);

-- Роль даты в цепочке одного события: эксперимент проведён ->
-- статья опубликована -> результат подтверждён -> дали премию.
create type date_role as enum (
    'occurred',     -- произошло / эксперимент поставлен
    'published',    -- опубликовано
    'confirmed',    -- независимо подтверждено
    'refuted',      -- опровергнуто
    'accepted',     -- стало общепринятым
    'patented',     -- патент выдан
    'awarded',      -- присуждена премия
    'other'
);

-- Рёбра графа «что к чему привело».
create type link_relation as enum (
    'led_to',       -- привело к
    'based_on',     -- опирается на
    'confirms',     -- подтверждает
    'refutes',      -- опровергает
    'related'       -- просто связано
);

-- ---------------------------------------------------------------------
-- Преобразование оси времени <-> календарь
--
-- PostgreSQL сам использует пролептический григорианский календарь
-- и поддерживает даты от 4713 до н.э., поэтому обе функции корректны
-- на всём диапазоне, который нам вообще может понадобиться.
-- ---------------------------------------------------------------------

create or replace function tl_day(d date) returns bigint
    language sql immutable strict parallel safe
    as $$ select (d - date '1970-01-01')::bigint $$;

create or replace function tl_date(n bigint) returns date
    language sql immutable strict parallel safe
    as $$ select date '1970-01-01' + n::int $$;

-- ---------------------------------------------------------------------
-- События
-- ---------------------------------------------------------------------

create table events (
    id              bigint generated always as identity primary key,

    -- Идентификатор в источнике: Q-номер Wikidata либо DOI из Crossref.
    -- NULL у записей, добавленных вручную.
    wikidata_id     text unique,

    -- Названия и описания лежат в event_translations: колонок вида
    -- title_ru / title_en на десять языков понадобилось бы сорок,
    -- и каждый новый язык означал бы миграцию.

    kind            event_kind not null default 'other',

    -- Ось времени. См. шапку файла.
    t_start         bigint not null,
    t_end           bigint not null,
    -- Точка, которой событие представлено на ленте. Именно по ней идёт
    -- бакетинг и позиционирование, поэтому она материализована и проиндексирована.
    t_mid           bigint generated always as ((t_start + t_end) / 2) stored,

    time_precision  date_precision not null default 'unknown',

    -- Приблизительная ли датировка («около 300 года до н. э.»).
    -- Сама подпись собирается на клиенте через Intl из интервала
    -- и точности, поэтому хранится только этот признак.
    circa           boolean not null default false,

    -- Календарь, в котором дата пришла из Wikidata ('julian' | 'gregorian').
    -- Сама дата уже пересчитана в григорианский; поле нужно, чтобы показать
    -- в карточке оговорку для событий до 1582 года.
    calendar_original text,

    -- Число языковых разделов Википедии — дешёвый и на удивление честный
    -- прокси значимости. significance = нормированный логарифм от него,
    -- по нему отбирается топ-K событий в бакете при отдалении.
    sitelinks       integer not null default 0,
    significance    real    not null default 0,

    image_url       text,
    wikipedia_ru    text,
    wikipedia_en    text,
    source_url      text,   -- DOI или ссылка на первоисточник

    -- Сырой ответ Wikidata. Разбор дат — самое хрупкое место импорта,
    -- без исходника отлаживать расхождения невозможно.
    raw             jsonb,

    created_at      timestamptz not null default now(),

    constraint events_interval_valid check (t_end > t_start),
    constraint events_significance_range check (significance >= 0 and significance <= 1)
);

-- Основной индекс ленты: диапазон по t_mid, а significance и kind лежат
-- в самом индексе, чтобы отбор топ-K в бакете не ходил в кучу.
create index events_t_mid_idx
    on events (t_mid) include (significance, kind);

-- Для запроса «какие события пересекают видимый диапазон» —
-- длинные интервалы (века, тысячелетия) по t_mid не находятся.
create index events_span_idx on events using gist (int8range(t_start, t_end));

-- Отдельная полоса «дата известна неточно» на мелких масштабах.
create index events_precision_idx on events (time_precision, t_mid);

-- ---------------------------------------------------------------------
-- Переводы
--
-- Отдельная таблица, а не колонки на каждый язык: языков десять,
-- и добавление одиннадцатого не должно требовать миграции схемы.
-- ---------------------------------------------------------------------

create table event_translations (
    event_id bigint not null references events(id) on delete cascade,
    lang     text   not null,
    title    text   not null,
    summary  text,

    -- Конфигурация поиска выбирается по языку строки. PostgreSQL 18
    -- не знает морфологии китайского и японского — для них остаётся
    -- simple, то есть поиск по точным словоформам.
    search_vector tsvector generated always as (
        to_tsvector(
            case lang
                when 'ru' then 'russian'::regconfig
                when 'en' then 'english'::regconfig
                when 'es' then 'spanish'::regconfig
                when 'fr' then 'french'::regconfig
                when 'de' then 'german'::regconfig
                when 'pt' then 'portuguese'::regconfig
                when 'ar' then 'arabic'::regconfig
                when 'hi' then 'hindi'::regconfig
                else 'simple'::regconfig
            end,
            coalesce(title, '') || ' ' || coalesce(summary, '')
        )
    ) stored,

    primary key (event_id, lang)
);

create index event_translations_search_idx on event_translations using gin (search_vector);
create index event_translations_lang_idx on event_translations (lang, event_id);

-- ---------------------------------------------------------------------
-- Цепочка дат одного события
--
-- Одно открытие живёт не в одной точке: эксперимент поставлен в марте,
-- статья вышла в ноябре, подтвердили через два года, премию дали через
-- двадцать. В events лежит «главная» дата, здесь — вся цепочка.
-- ---------------------------------------------------------------------

create table event_dates (
    id              bigint generated always as identity primary key,
    event_id        bigint not null references events(id) on delete cascade,
    role            date_role not null,

    t_start         bigint not null,
    t_end           bigint not null,
    time_precision  date_precision not null default 'unknown',

    display_ru      text,
    display_en      text,
    note_ru         text,
    note_en         text,

    constraint event_dates_interval_valid check (t_end > t_start)
);

create index event_dates_event_idx on event_dates (event_id);
create index event_dates_t_idx on event_dates (t_start);

-- ---------------------------------------------------------------------
-- Граф «что к чему привело»
-- ---------------------------------------------------------------------

create table event_links (
    from_event_id bigint not null references events(id) on delete cascade,
    to_event_id   bigint not null references events(id) on delete cascade,
    relation      link_relation not null,

    primary key (from_event_id, to_event_id, relation),
    constraint event_links_no_self check (from_event_id <> to_event_id)
);

create index event_links_to_idx on event_links (to_event_id, relation);

-- ---------------------------------------------------------------------
-- Области науки
-- ---------------------------------------------------------------------

create table categories (
    id       smallint generated always as identity primary key,
    slug     text not null unique,
    name_ru  text not null,
    name_en  text not null,
    color    text not null   -- цвет точки на ленте, hex
);

create table event_categories (
    event_id    bigint   not null references events(id) on delete cascade,
    category_id smallint not null references categories(id) on delete cascade,
    primary key (event_id, category_id)
);

-- Обратный порядок ключа: фильтр по категории — самый частый запрос ленты.
create index event_categories_cat_idx on event_categories (category_id, event_id);

-- ---------------------------------------------------------------------
-- Учёные
-- ---------------------------------------------------------------------

create table people (
    id          bigint generated always as identity primary key,
    wikidata_id text unique,
    name_ru     text,
    name_en     text,
    image_url   text,

    constraint people_has_name check (name_ru is not null or name_en is not null)
);

create table event_people (
    event_id  bigint not null references events(id) on delete cascade,
    person_id bigint not null references people(id) on delete cascade,
    primary key (event_id, person_id)
);

create index event_people_person_idx on event_people (person_id);

commit;
