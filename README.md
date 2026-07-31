# Science History Timeline

An interactive timeline of scientific discoveries, inventions, publications and
prizes. Smooth zoom from millennia down to single days, clustering when zoomed
out, a card on hover, filters by field of science, ten interface languages, light
and dark themes. The data comes from Wikidata and Crossref.

```
  −  millennia  centuries  decades  years  months  days  +

          DISCOVERY           CONFIRMATION
              ●                     ●
   ───────────┼─────────────────────┼──────────────
      July 28        July 29            July 30

                       ●
                   REFUTATION
```

## How it is put together

| Part | Stack | Purpose |
|---|---|---|
| `src/ScienceTimeline.Core` | .NET 10 | The time axis and Wikidata date parsing. Covered by tests |
| `src/ScienceTimeline.Etl` | .NET 10, Npgsql | Import from Wikidata and Crossref, export to static files |
| `web` | Vite, TypeScript, Canvas | The timeline itself |
| `db` | PostgreSQL 18 | Schema and the reference list of scientific fields |
| `src/ScienceTimeline.Api` | .NET 10, Dapper | Optional server mode, not used by the published site |

**The published site is static.** The database and .NET are only needed while
building the data: the import fills PostgreSQL, the export turns it into a few
JSON files, and from there the browser computes buckets, clusters and search on
its own. Slicing the data into tiles per zoom level makes no sense — the whole
set is smaller than a single photo.

`ScienceTimeline.Api` remains in the repository as a working alternative for
anyone who needs a server with a database, but GitHub Pages does not run it and
deployment gets by without it.

## Five decisions everything rests on

### 1. Time is a number, not a date

The axis is stored as a signed `bigint` — whole days since 1970-01-01. Not
`date`.

That buys three things a calendar type cannot give you:

- BC dates work without special cases;
- imprecise dating is simply a wide half-open interval `[t_start, t_end)` rather
  than an invented January 1st. An event "around 300 BC" occupies a century, and
  the timeline shows that;
- zooming at any scale is an ordinary range scan over a btree index.

Internally everything is computed via the Julian day number, so Julian and
Gregorian dates land on the same axis without a separate conversion step: it is
enough to parse each one with its own calendar's formula.

The axis is duplicated in `web/src/timeAxis.ts` — the numbers must match to the
day, otherwise the timeline drifts away from the data.

### 2. Parsing Wikidata dates — three traps

Each one quietly corrupts the data, which is why `WikidataTime` is covered by
tests:

- **Year numbering.** Wikidata writes 44 BC as `-0044` — its counting has no year
  zero. In astronomical numbering that is the year −43. An off-by-one shifts all
  of antiquity.
- **Calendar.** Before 1582 dates usually arrive as Julian
  (`calendarModel = Q1985786`). Parsing them with the Gregorian formula moves an
  event by 10–13 days.
- **Zero month and day.** At coarse precision you get `+1905-00-00`.

Centuries and millennia are computed using historical counting, where there is no
year zero: the 20th century is 1901–2000, and the 1st century BC ends at AD 1.
Naively rounding down to the hundred would label the year 1900 as "20th century".

### 3. Astronomy is separated from the history of science

Wikidata holds 34 thousand asteroids with an exact discovery date against a
couple of thousand genuine scientific events. An "import everything with P575"
approach yields a timeline that is 95% nameless minor planets.

So astronomical objects go through a separate significance threshold. The value
45 was picked from the actual distribution rather than by eye: below it astronomy
wins on sheer count (6,906 objects against 2,698 of all other events), above it
the ratio evens out. Pluto, Halley's Comet and Ceres stay; "1998 QE2" does not.

Significance is measured by the number of Wikipedia language editions. That is a
cheap and surprisingly honest proxy: the same number selects the top-K events in
a bucket when the timeline is zoomed out and not all points fit.

### 4. Wikidata does not know recent science — a second source is needed

Wikidata describes the history of science well and the present almost not at all.
Measured against the database: for the whole of 2025 it had **twelve** events,
and for 2026 none at all. A discovery reaches Wikidata months or years after
publication, so the timeline used to trail off years before today even though it
opened on the current week.

The second source is Crossref. It knows every paper with a DOI, but there are
millions per year and taking them all is pointless: the result would be a
bibliography, not a history of science. Selection goes by the ISSNs of a couple
of dozen leading journals — Nature, Science, Cell, PNAS, The Lancet, NEJM,
Physical Review Letters and others. The filter is crude but honest: there is no
free signal of importance for a paper that has just come out, and citations
appear years later.

The query is always sorted by date descending. Those journals produce about two
thousand papers a month, the result limit is hit almost every time, and without
sorting it would cut the output at an arbitrary point — throwing away exactly
what the second source was added for.

### 5. Date labels are assembled on the client, not stored in the database

There are ten languages. Keeping `date_display_ru`, `date_display_en` and eight
more columns would mean reimporting all the data just to add an eleventh
language.

So the database holds only the interval, the precision and an "approximate" flag,
while the label is assembled by `Intl.DateTimeFormat` in the browser — it knows
month names and era markers for every locale, including BC years. Intl cannot
handle decades, centuries and millennia, so those are assembled by functions:
Russian puts a Roman numeral before the word, German an Arabic one followed by a
period, and Chinese and Japanese write the era as a prefix.

Event titles and descriptions are moved out of `events` into an
`event_translations` table for the same reason.

## Running locally

You need the .NET 10 SDK, PostgreSQL 18 and Node.

```bash
psql -U postgres -c "create database science_timeline encoding 'UTF8'"
```

```bash
psql -U postgres -d science_timeline -f db/schema.sql -f db/seed_categories.sql
```

If the database was created before translations were added, apply the migration
instead:

```bash
psql -U postgres -d science_timeline -f db/migrate_001_translations.sql
```

Import (about 10 minutes — WDQS rate-limits requests):

```bash
dotnet run --project src/ScienceTimeline.Etl -c Release -- --fresh
```

Export the database to the static files the site reads:

```bash
dotnet run --project src/ScienceTimeline.Etl -c Release -- --export web/public/data
```

```bash
npm --prefix web run dev
```

Open http://localhost:5173

The connection string is taken from the `SCIENCE_TIMELINE_DB` variable, defaulting
to a local PostgreSQL with the `postgres` user.

### Export format

| File | What is inside |
|---|---|
| `meta.json` | time bounds, the list of fields, event kinds, languages |
| `core.json` | the numeric core of all events as columns, shared by all languages |
| `text-<lang>.json` | titles and descriptions, aligned with the core's ordering |

Columns rather than an array of objects: thousands of repeats of the
`significance` key compress badly, while a column of uniform numbers compresses
very well. Text is split from the core so that switching languages only fetches
the text file. Fields of science are stored as a bit mask: there are eleven
categories, they fit into a single integer, and the client-side filter becomes a
bitwise AND.

### Import options

| Option | Meaning | Default |
|---|---|---|
| `--min-sitelinks N` | significance threshold | 5 |
| `--astro-min N` | separate threshold for astronomy | 45 |
| `--limit N` | keep the N most significant events | no limit |
| `--fresh` | clear events before importing | no |
| `--no-nobel` | skip Nobel prizes | no |
| `--no-crossref` | skip recent publications | no |
| `--crossref-since D` | the date to pull publications from | one year ago |
| `--crossref-limit N` | cap on the number of publications | 20000 |

## What is computed in the browser

Everything SQL used to do: range selection (binary search over the sorted
`tMid`), bucketing, per-kind counters, top-K by significance, and search. On
twenty-five thousand events that takes fractions of a millisecond.

The only loss compared with the server version is search morphology. PostgreSQL
knew that «квантовая» and «квантовый» are the same word; substring search does
not. In exchange it is instant and works offline.

## What used to be the server

`ScienceTimeline.Api` implements the same thing on PostgreSQL — bucketing in a
single query with a window function over the
`events (t_mid) include (significance, kind)` index, and full-text search with
morphology for eight languages. The published site does not need it, but it stays
functional in case a server-backed variant is ever wanted.

## Tests

```bash
dotnet test
```

44 tests for the time axis and date parsing. The expected values were checked
against PostgreSQL itself (`select tl_day(...)`) so that the C# axis and the
database axis are guaranteed to agree.

## Deployment

The free option: frontend on Vercel or Cloudflare Pages, backend as a container
on Google Cloud Run (always-free, scale-to-zero), database on Neon (0.5 GB, no
idle suspension).

.NET does not run on Vercel: there is no such runtime there, only Node, Python,
Go and Ruby.

The 0.5 GB limit is a design constraint, not a detail. The database holds only
the title, a short description, dates, category, significance and links; images
and full texts are pulled from Wikipedia on demand.

## Languages

Interface and data: English, Chinese, Hindi, Spanish, Arabic, French, Portuguese,
Russian, German, Japanese. They were picked by number of speakers, adjusted for
representation in Wikidata: Bengali and Urdu have more speakers than German, but
several times fewer labels, and the timeline would come out empty.

Arabic flips the interface right-to-left; the timeline itself stays
left-to-right, because time in it flows one way regardless of the writing system.

Full-text search uses morphology for eight of the ten languages — PostgreSQL 18
has no dictionaries for Chinese and Japanese, so search there matches exact word
forms. A query always searches both in the interface language and in English.

## What is next

- `event_dates` and `event_links` exist in the schema, but the import does not
  populate them yet: this needs the "experiment → publication → confirmation →
  prize" chain and a "what led to what" graph
- refutations (`refutation`) are barely annotated in Wikidata — a separate source
  is needed
- Crossref publications only have an English title: there is nothing to translate
  paper titles with, and the API returns them with a fallback
- scientists' names are stored only in Russian and English, even though the
  import fetches them in all ten languages
- Native AOT for a fast cold start: would require replacing Dapper with
  Dapper.AOT or bare Npgsql
