-- =====================================================================
-- Миграция 001: переводы вынесены в отдельную таблицу
--
-- Было: колонки title_ru / title_en / summary_ru / summary_en прямо
-- в events. Для двух языков это терпимо, для десяти — сорок колонок
-- и новая миграция на каждый добавленный язык.
--
-- Заодно исчезают date_display_ru / date_display_en: подпись даты
-- теперь собирается на клиенте через Intl из интервала и точности.
-- Хранить её строкой означало бы держать по строке на язык и
-- переимпортировать все данные ради одиннадцатого языка. Признак
-- «около» при этом потерялся бы, поэтому он переезжает в отдельную
-- колонку circa.
--
-- Идемпотентна: можно применять к уже мигрированной базе.
-- =====================================================================

begin;

create table if not exists event_translations (
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

create index if not exists event_translations_search_idx
    on event_translations using gin (search_vector);

-- Отбор перевода идёт по (event_id, lang), это и есть первичный ключ.
-- Отдельный индекс нужен для обратного направления: «все события,
-- у которых вообще есть перевод на такой-то язык».
create index if not exists event_translations_lang_idx
    on event_translations (lang, event_id);

-- Признак приблизительной датировки. Раньше «около» было вшито
-- в готовую строку даты.
alter table events add column if not exists circa boolean not null default false;

-- Переносим то, что уже импортировано, чтобы не гонять импорт заново.
do $$
begin
    if exists (select 1 from information_schema.columns
               where table_name = 'events' and column_name = 'title_ru') then

        insert into event_translations (event_id, lang, title, summary)
        select id, 'ru', title_ru, summary_ru from events where title_ru is not null
        on conflict do nothing;

        insert into event_translations (event_id, lang, title, summary)
        select id, 'en', title_en, summary_en from events where title_en is not null
        on conflict do nothing;

        -- «около 300 года до н. э.» -> circa = true
        update events set circa = true
        where date_display_ru like 'около %' or date_display_en like 'c. %';
    end if;
end $$;

alter table events drop column if exists search_vector;
alter table events drop column if exists title_ru;
alter table events drop column if exists title_en;
alter table events drop column if exists summary_ru;
alter table events drop column if exists summary_en;
alter table events drop column if exists date_display_ru;
alter table events drop column if exists date_display_en;

commit;
