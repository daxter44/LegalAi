# Runbook: polski słownik do wyszukiwania pełnotekstowego (hunspell) na maszynie z korpusem

Odbiorca: agent/operator na maszynie produkcyjnej z pełnym korpusem (`.11`, RTX 3060). Cel: włączyć
polską lematyzację w torze rzadkim (leksykalnym) retrievalu, zmierzyć zysk i koszt, i **zaraportować
liczby** — decyzja o pozostawieniu zmiany zapada po pomiarze, nie przed.

Kontekst z przeglądu: `docs/PRZEGLAD-JAKOSC-I-LATENCJA-2026-08-14.md`, pozycja **P7**.

---

## 1. Problem w jednym akapicie

Kolumna `chunks."SearchVector"` jest liczona konfiguracją `simple`, która **nie robi stemmingu i nie
usuwa stopwordów**, a tor rzadki pyta przez `websearch_to_tsquery`, które **AND-uje wszystkie tokeny**.
W polskim (język fleksyjny) to znaczy, że pytanie zadane inną odmianą niż tekst przepisu nie dopasowuje
niczego — łącznie z chunkiem, który jest dosłowną odpowiedzią. Tor rzadki jest więc prawdopodobnie
martwy na pytaniach opisowych, a „hybrydowy" retriever realnie jest dense-only i fuzja RRF miesza jeden
ranking z pustką.

## 2. Co zostało zweryfikowane, a co nie

Zweryfikowane **lokalnie na PostgreSQL 17.10 (ten sam obraz bazowy)**, na literalnych `to_tsvector`
i `tsquery` — czyli własności silnika i słownika, niezależne od zawartości korpusu. **Przenosi się** na
maszynę z korpusem:

| test | `simple` (dziś) | `polish` (hunspell) |
|---|---|---|
| „przedawnienie roszczenia" ↔ „przedawnienia roszczeń" | `f` | **`t`** |
| „miasto" ↔ „mieście" (wymiana w rdzeniu) | `f` | **`t`** |
| pytanie ↔ chunk z inną odmianą wszystkich słów | `f` | **`t`** |
| pytanie z parafrazą („z winy własnej" vs „z winy swojej") | `f` | `f` |

Lematyzacja działa: `pracowników→pracownik`, `rozmów→rozmowa`, `może→móc`, `mieście→miasto`.

**NIE zweryfikowane i to jest zadanie tego runbooka:** jak często to boli na realnych pytaniach
i **ile kosztuje czasowo przy pełnym korpusie**. Lokalna baza deweloperska miała 39 dokumentów, więc
o latencji nie mówi nic. Nie ekstrapolować — wąskim gardłem jest liczba wierszy dopasowanych przez
zapytanie leksykalne, a to zależy od rozkładu tokenów w korpusie.

## 3. Trzy pułapki — przeczytać PRZED zmianami

**3.1. Debianowy `hunspell-pl` jest w ISO-8859-2, a `pg_updatedicts` NIE konwertuje.** Po instalacji
pakietu `postgresql-common` samo tworzy symlinki w `tsearch_data`, ale plik `.affix` zostaje
z nagłówkiem `SET ISO8859-2`. W bazie UTF-8 daje to brak dopasowań. `iconv` jest obowiązkowy — jest
w `infra/Dockerfile.db`, z asercją `head -1 | grep -qx 'SET UTF-8'` na końcu warstwy.

**3.2. Stoplista dla słowników ispell działa w przestrzeni LEMATÓW, nie form powierzchniowych.**
Dowód: „przez" (lemat = `przez`, jest na liście) → usuwane; „czy" → lemat `cza`, nie ma na liście →
**zostaje w wektorze**. Polskie słowa funkcyjne lematyzują się nieintuicyjnie:

```
bez→beza   czy→cza   lub→luba,lubić   od→oda   tak→taka
ile→ił     jak→jaka  tego,tej→ty      tylko→tylka   niż→nizać   ale→al,ala
```

Dlatego `infra/tsearch/polish.stop` zawiera wyrazy, które wyglądają jak pomyłka (`beza`, `cza`, `ił`,
`tylka`). **Nie „porządkować" tego pliku.** Jeden nieusunięty stopword wywala całe dopasowanie, bo
`websearch_to_tsquery` AND-uje wszystko — to była przyczyna, dla której pierwsza wersja setupu nadal
nie działała.

**3.3. Słownik sam NIE wystarczy.** Ostatni wiersz tabeli w sekcji 2: po dodaniu słownika pytanie
„kto wyrządził szkodę **z winy własnej**" nadal nie trafia w art. 415 KC („z winy **swojej**"), bo AND
wymaga każdego słowa. Pełna naprawa = słownik (odmiana) **plus** zmiana operatora na OR z rankingiem
(parafraza). Ten runbook robi TYLKO słownik — świadomie, żeby dało się rozdzielić w pomiarach, co dał
słownik, a co zmiana semantyki dopasowania.

## 4. Krok 0 — kontrole wstępne (nic nie zmieniają)

```sql
-- 4a. KRYTYCZNE, niezależne od tego zadania: czy tor gęsty ma właściwy indeks?
SELECT indexdef FROM pg_indexes WHERE tablename='chunks' AND indexname='IX_chunks_Embedding';
```

**Musi zawierać `halfvec_cosine_ops`.** Jeśli widzisz `"Embedding" vector_cosine_ops` (wariant fp32) —
**PRZERWIJ ten runbook i zaraportuj**. Zapytanie toru gęstego rzuca obie strony na `halfvec`, a indeks
fp32 takiego wyrażenia nie obsługuje → sequential scan po całej tabeli przy każdym pytaniu. To problem
o rząd wielkości większy niż tor rzadki. Naprawa: migracja `SyncHalfvecEmbeddingIndex` (jest
w repo, jest no-opem gdy indeks już jest poprawny) albo krok 9 `RUNBOOK-3060-DOCKER.md`.

```sql
-- 4b. Skala (raportuj — dokumenty mówią o 7,4 mln, w rozmowie pojawiło się 16 mln)
SELECT (SELECT count(*) FROM chunks) AS chunks, (SELECT count(*) FROM documents) AS documents;

-- 4c. Kodowanie bazy MUSI być UTF8 (od tego zależy konwersja słownika)
SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database();

-- 4d. Wersja i rozmiary — potrzebne do oceny, czy jest miejsce na nowy indeks GIN
SELECT version();
SELECT pg_size_pretty(pg_total_relation_size('chunks')) AS chunks_total,
       pg_size_pretty(pg_relation_size('IX_chunks_SearchVector')) AS gin_dzisiejszy;
```

```bash
# 4e. Wolne miejsce na dysku. Nowy indeks GIN powstaje OBOK starego (oba istnieją naraz),
#     więc potrzebny zapas >= rozmiar dzisiejszego GIN-a razy ~2.
df -h /var/lib/postgresql/data
```

## 5. Krok 1 — POMIAR „PRZED" (jeszcze bez żadnych zmian)

To najważniejszy pomiar w całym runbooku i jest darmowy. Jeśli tor rzadki już dziś trafia we właściwe
artykuły, całe zadanie jest bezcelowe. Metryka: dla każdego pytania z golden setu — czy zapytanie
leksykalne dopasowuje chunk **właściwego artykułu** (`expectedEli` + `expectedArticle`).

Wygeneruj listę pytań z `src/PrawoRAG.Eval/golden-set.json` (18 pozycji, pola `id`, `question`,
`expectedEli`, `expectedArticle`) do bloku `VALUES`, np. przez `jq`, i podstaw do szablonu:

```sql
SET statement_timeout = '120s';   -- bezpiecznik: żaden pomiar nie może wisieć

WITH pytania(id, q, eli, art) AS (VALUES
  ('kk-148', 'Jaka kara grozi za zabójstwo człowieka?', 'DU/1997/553', '148'),
  ('kp-52',  'Kiedy pracodawca może rozwiązać umowę o pracę bez wypowiedzenia z winy pracownika?', 'DU/1974/141', '52')
  -- ... pozostałe pozycje z golden-set.json (category = "InCorpus")
)
SELECT p.id,
  -- czy trafia we WŁAŚCIWY artykuł (to jest metryka; filtr po metadanych trzyma zapytanie tanim)
  (SELECT count(*) FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
    WHERE d."ExternalId" = p.eli AND c."ArticleNo" = p.art
      AND c."SearchVector" @@ websearch_to_tsquery('simple', p.q)) AS wlasciwy_artykul,
  -- ile chunków W OGÓLE dopasowuje (ucięte, żeby nie liczyć milionów)
  (SELECT count(*) FROM (SELECT 1 FROM chunks c
      WHERE c."SearchVector" @@ websearch_to_tsquery('simple', p.q) LIMIT 1000) t) AS trafien_do_1000
FROM pytania p ORDER BY 1;
```

**Zapisz wynik.** Oczekiwanie z analizy: `wlasciwy_artykul = 0` dla większości pytań. Jeśli tak — masz
dowód, że tor jest martwy, i to bez żadnej przebudowy.

## 6. Krok 2 — obraz z słownikiem

Artefakty są w repo: `infra/Dockerfile.db` i `infra/tsearch/polish.stop`. **Obraz został zbudowany
i przetestowany dymnie** (asercje kodowania w Dockerfile przechodzą, świeży kontener z tego obrazu
przeszedł weryfikacje z kroku 3 i 4) — jeśli u ciebie wynik odbiega od „oczekiwanego", to różnica
środowiska, nie literówka w przepisie.

`infra/compose.yaml` **nie jest** w repo przełączony na własny build — celowo, żeby stockowy obraz dalej
działał na maszynach deweloperskich (w repo nie ma jeszcze migracji tworzącej konfigurację `polish`,
więc stock jest nadal poprawny). Na tej maszynie podmień usługę `db`:

```yaml
  db:
    build:
      context: .                 # katalog infra/
      dockerfile: Dockerfile.db
    # image: docker.io/pgvector/pgvector:pg17   # <- zakomentowane, zostaje jako referencja
```

```bash
cd infra
podman compose build db
podman compose up -d db
```

**Wolumen z danymi nie jest ruszany** — zmienia się tylko obraz. Ale to restart bazy, więc zrób to
w oknie, w którym nikt nie korzysta z czatu.

## 7. Krok 3 — weryfikacja plików w kontenerze

```bash
podman compose exec db bash -lc '
TS="$(pg_config --sharedir)/tsearch_data"
head -1 "$TS/pl_pl.affix"          # MUSI byc: SET UTF-8
ls -la "$TS"/pl_pl.* "$TS"/polish.stop
wc -l "$TS/polish.stop"            # MUSI byc: 94
grep -cx "beza\|cza\|oda" "$TS/polish.stop"   # MUSI byc: 3 (lematy-pulapki obecne)
'
```

Jeśli `.affix` pokazuje `SET ISO8859-2` — obraz zbudował się ze starej warstwy cache. `podman compose
build --no-cache db`.

## 8. Krok 4 — obiekty konfiguracji w bazie

Pliki daje obraz, ale słownik i konfiguracja to obiekty **per-baza** — trzeba je utworzyć w bazie
`praworag`. Na razie ręcznie (migracji EF świadomie nie ma w repo, patrz sekcja 12):

```sql
CREATE TEXT SEARCH DICTIONARY polish_hunspell (
  TEMPLATE  = ispell,
  DictFile  = pl_pl,
  AffFile   = pl_pl,
  StopWords = polish
);
CREATE TEXT SEARCH CONFIGURATION polish (COPY = simple);
ALTER TEXT SEARCH CONFIGURATION polish
  ALTER MAPPING FOR asciiword, asciihword, hword_asciipart, word, hword, hword_part
  WITH polish_hunspell, simple;
```

Weryfikacja — **te trzy zapytania muszą dać dokładnie te wyniki**:

```sql
SELECT to_tsvector('polish', 'przedawnienia roszczeń majątkowych');
--  oczekiwane (zmierzone na tym obrazie):
--  'majątkowy':3 'przedawnienie':1 'przedawnić':1 'roszczenie':2 'rościć':2

SELECT to_tsvector('polish', 'czy pracodawca może nagrywać rozmowy pracownika');
--  oczekiwane: 'nagrywać':4 'pracodawca':2 'pracownik':6 'rozmowa':5
--  (BRAK 'cza' i 'móc' = stoplista dziala w przestrzeni lematow — patrz 3.2)

SELECT to_tsvector('polish','Termin przedawnienia roszczeń majątkowych wynosi sześć lat.')
       @@ websearch_to_tsquery('polish','przedawnienie roszczenia majątkowego') AS musi_byc_t;
--  oczekiwane: t
```

**Dlaczego jeden token daje po DWA lematy** (`przedawnienie` i `przedawnić`, `roszczenie` i `rościć`):
hunspell zwraca wszystkie możliwe lematy przy niejednoznaczności. To jest w porządku i działa na korzyść
recallu, bo w zapytaniu alternatywy są **OR-owane w obrębie tokenu**, a AND-owane tylko między tokenami:

```sql
SELECT websearch_to_tsquery('polish','przedawnienie roszczenia majątkowego')::text;
--  ( 'przedawnienie' | 'przedawnić' ) & ( 'roszczenie' | 'rościć' ) & 'majątkowy'
```

Czyli słownik nie zwęża dopasowania — jedynym zwężeniem zostaje `&` między tokenami, i to jest dokładnie
to, co opisuje pułapka 3.3. Nie „naprawiać" wieloznaczności.

Jeśli w drugim zapytaniu widzisz `'cza'` — stoplista nie została wczytana (zły plik / złe kodowanie /
literówka w `StopWords = polish`). Nie idź dalej.

## 9. Krok 5 — indeks wyrażeniowy (BEZ przepisywania tabeli)

**Nie zmieniaj definicji kolumny generowanej `SearchVector`.** `ALTER TABLE ... ALTER COLUMN ... SET
EXPRESSION` (PG17) oraz drop+add przepisują CAŁĄ tabelę — przy tej skali to godziny i podwojone
zapotrzebowanie na dysk. Zamiast tego indeks wyrażeniowy, czyli ten sam wzorzec, który już działa
na `halfvec`:

```sql
SET maintenance_work_mem = '4GB';               -- podnieś jeśli jest wolny RAM
SET max_parallel_maintenance_workers = 0;       -- jak przy HNSW: stabilniej w tym środowisku
SET statement_timeout = 0;                      -- budowa indeksu nie może zostać ubita

CREATE INDEX "IX_chunks_SearchVector_pl" ON chunks
  USING gin (to_tsvector('polish', coalesce("Text", '')));
```

Uwaga techniczna: musi być **dwuargumentowy** `to_tsvector('polish', …)`. Wariant jednoargumentowy jest
tylko `STABLE` (zależy od `default_text_search_config`) i indeksu z niego nie zbudujesz.

Oczekiwany czas: GIN na 7,4 mln chunków budował się **15 min 20 s** (`SESJA-2026-07-17`), więc przy
16 mln rząd **35–40 min**. Po zakończeniu zaraportuj czas i rozmiar:

```sql
SELECT pg_size_pretty(pg_relation_size('IX_chunks_SearchVector_pl'));
```

Rozważ `CREATE INDEX CONCURRENTLY`, jeśli baza musi w tym czasie obsługiwać czat (dłużej, ale bez
blokowania zapisów).

## 10. Krok 6 — POMIAR „PO"

**6a. Jakość — ten sam pomiar co w kroku 1, tylko na `polish`:**

```sql
SET statement_timeout = '120s';
WITH pytania(id, q, eli, art) AS (VALUES /* ta sama lista co w kroku 1 */)
SELECT p.id,
  (SELECT count(*) FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
    WHERE d."ExternalId" = p.eli AND c."ArticleNo" = p.art
      AND to_tsvector('polish', coalesce(c."Text",'')) @@ websearch_to_tsquery('polish', p.q))
    AS wlasciwy_artykul,
  (SELECT count(*) FROM (SELECT 1 FROM chunks c
      WHERE to_tsvector('polish', coalesce(c."Text",'')) @@ websearch_to_tsquery('polish', p.q)
      LIMIT 1000) t) AS trafien_do_1000
FROM pytania p ORDER BY 1;
```

**6b. Latencja — to jest odpowiedź na pytanie „czy sprzęt udźwignie".** Dla 5–10 pytań z golden setu,
w kształcie realnego zapytania toru rzadkiego (dopasowanie + ranking + `LIMIT`):

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT c."Id", ts_rank(to_tsvector('polish', coalesce(c."Text",'')),
                       websearch_to_tsquery('polish', 'TU PYTANIE')) AS rank
FROM chunks c
WHERE to_tsvector('polish', coalesce(c."Text",'')) @@ websearch_to_tsquery('polish', 'TU PYTANIE')
ORDER BY rank DESC
LIMIT 50;
```

Raportuj per pytanie: **czy plan używa `IX_chunks_SearchVector_pl`** (a nie Seq Scan), liczbę
dopasowanych wierszy, `Execution Time`. Uwaga na znaną patologię, którą sami zmierzyliście w torze
akronimowym: pospolity token dopasowuje setki tysięcy chunków i wtedy `ORDER BY ts_rank` po takim
zbiorze jest kosztowny. Jeśli `Execution Time` przekracza ~1 s na typowym pytaniu, to jest realny
sygnał, że sprzęt tego nie udźwignie w tej formie.

**6c. Pamięć — słownik ładuje się do pamięci KAŻDEGO backendu przy pierwszym użyciu w sesji.** Lokalnie
zmierzone **+31 MB/backend** (12 MB → 43 MB; dla `simple`: 12 → 15 MB). Potwierdź na tej maszynie
w świeżej sesji:

```sql
SELECT (substring(pg_read_file('/proc/self/status') from 'VmRSS:\s+(\d+)'))::int / 1024 AS rss_mb;
SELECT to_tsvector('polish','przedawnienie roszczeń') IS NOT NULL;
SELECT (substring(pg_read_file('/proc/self/status') from 'VmRSS:\s+(\d+)'))::int / 1024 AS rss_mb;
```

Pomnóż różnicę przez rozmiar puli połączeń i zestaw z wolnym RAM-em (środowisko WSL2 ma realny sufit
~15 GB, a TEI i tak coś zjada). Jeśli wychodzi ciasno — jest rozszerzenie `shared_ispell` (słownik
w pamięci współdzielonej), ale to kolejna zależność w obrazie.

## 11. Co zaraportować

Krótka lista, na podstawie której podejmiemy decyzję:

1. Krok 0: definicja `IX_chunks_Embedding` (halfvec czy fp32), liczba chunków i dokumentów, wolne miejsce.
2. Krok 1 vs 6a: tabelka `wlasciwy_artykul` przed i po — ile z 18 pytań trafia we właściwy artykuł.
3. Krok 5: czas budowy i rozmiar indeksu GIN.
4. Krok 6b: dla każdego testowanego pytania — indeks użyty czy Seq Scan, liczba dopasowań, czas.
5. Krok 6c: przyrost RSS na backend.
6. Cokolwiek, co odbiegło od „oczekiwanych" wyników w krokach 3 i 4.

## 12. Czego NIE robić

- **Nie zmieniać definicji kolumny `SearchVector`** (przepisanie tabeli — patrz krok 5).
- **Nie commitować migracji EF tworzącej konfigurację `polish`, dopóki obraz nie jest wszędzie.**
  Obiekty konfiguracji są per-baza, a pliki są w obrazie: gdy ktoś podniesie stockowy
  `pgvector/pgvector:pg17` przeciw bazie z konfiguracją `polish`, każde zapytanie leksykalne padnie na
  „could not open dictionary file", a **odtworzenie dumpa nie przejdzie**, bo nie da się przebudować
  indeksu wyrażeniowego. Obraz i baza stają się wersjonowane razem — to celowy dług, spłacany dopiero
  przy przełączeniu kodu.
- **Nie odpalać `Down` migracji `SyncHalfvecEmbeddingIndex`** — odtwarza indeks HNSW w wariancie fp32,
  który w tym środowisku jest niebudowalny (policzone ~33 GB grafu przy realnym sufcie ~15 GB RAM).
- **Nie usuwać starego indeksu ani kolumny `SearchVector`** w tym przebiegu. Dopóki kod ich używa, to
  jedyna działająca ścieżka; sprzątanie następuje po przełączeniu kodu i po pomiarach.

## 13. Rollback

Zmiany są addytywne i odwracalne bez utraty danych:

```sql
DROP INDEX IF EXISTS "IX_chunks_SearchVector_pl";
DROP TEXT SEARCH CONFIGURATION IF EXISTS polish;
DROP TEXT SEARCH DICTIONARY IF EXISTS polish_hunspell;
```

Obraz: przywróć `image:` w `compose.yaml` i `podman compose up -d db`. Kod nie był ruszany, więc tor
rzadki dalej działa na `simple` i kolumnie `SearchVector` — po rollbacku system wraca dokładnie do
stanu sprzed runbooka.

## 14. Co dalej, jeśli pomiary wyjdą dobrze

Osobne zadanie, świadomie nie w tym runbooku (żeby pomiar rozdzielał przyczyny):

1. Przełączenie toru rzadkiego i akronimowego na surowe SQL z wyrażeniem `to_tsvector('polish', …)` —
   LINQ tego nie wyrazi, ale `DenseAsync` już dziś jest surowym SQL, więc wzorzec istnieje.
2. Decyzja o operatorze: OR z rankingiem zamiast AND (pułapka 3.3 — bez tego parafrazy dalej nie
   trafiają), z osobnym pomiarem, bo to zmiana semantyki dopasowania.
3. Migracja EF + test-strażnik (w stylu `HalfvecIndexTests`) asertujący, że
   `to_tsvector('polish','roszczeń')` daje `roszczenie` — inaczej wracamy do klasy problemów
   „psuje się cicho", którą zamykaliśmy w P1.
4. Sprzątanie: `DROP INDEX IX_chunks_SearchVector`, `DROP COLUMN SearchVector`.
