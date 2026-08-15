# Diagnoza: tor rzadki (BM25/FTS) i polski lematyzator — pełna historia i wnioski

**Data:** 2026-08-11 → 2026-08-15
**Status:** zamknięte na dziś — obiekty w bazie zbudowane i zmierzone, integracja z kodem
świadomie NIE wysłana (patrz sekcja 6).

## 1. Punkt wyjścia: konkretna skarga na retrieval

Pytanie użytkownika: *"Czy przedszkola muszą dokonywać opłaty za abonament RTV?"* — powinno
zwrócić `Ustawa z dnia 21 kwietnia 2005 r. o opłatach abonamentowych, art. 2` (Dz.U. 2005 nr 85
poz. 728, `DU/2005/728`). Dokument jest w korpusie, jest merytorycznie trafny — ale nie wychodził
w źródłach.

Ogólniejszy problem zgłoszony przez użytkownika: *"cel to poprawić retrieval, bo poprawne
dokumenty, które często są w bazie, trafiają na miejscu 85 czy 100"*.

## 2. Warstwa 1 diagnozy: HNSW `ef_search` (naprawione, wysłane)

Zmierzono bezpośrednio (cosine na `halfvec(1024)`, dokładnie ten sam wariant co produkcja):

| chunk | prawdziwa ranga w całym korpusie | widoczny w top-100/200 przy `ef_search=400`? |
|---|---|---|
| art. 56 KRO | #14 | NIE |
| art. 2 ustawy o opłatach abonamentowych | #38 | NIE |

Przy `ef_search=1000` (maksimum dopuszczalne przez pgvector) oba wchodzą do puli (#37, #65),
koszt +~7ms/zapytanie (10ms→17ms, EXPLAIN ANALYZE) — nieodczuwalne na tle
embedding+reranker+LLM liczonych w sekundach.

**Wysłane:** `HnswEfSearch = 400 → 1000` w `HybridRetriever.cs`, commit `28b50f3`.

To naprawiło **jedną** przyczynę (HNSW gubił dobre wektory), ale nie całość: nawet po tej
poprawce ten sam chunk (art. 2 ustawy o opłatach abonamentowych) miał rangę fuzji RRF #37 —
tuż **za** cutoffem `Take(TopK×4=32)` post-fuzji. Eksperyment z podniesieniem mnożnika do
`TopK×8` faktycznie wciągał chunk do puli, ale kosztem +1,8–2,2s/zapytanie (dwa sekwencyjne
batche rerankera po 32 zamiast jednego) — **nie wysłane**, koszt uznany za zbyt wysoki bez
dalszej pracy nad batchowaniem rerankera.

## 3. Warstwa 2: `RankCoverDensity` (ts_rank_cd) — zbadane i odrzucone

Zbadano `ts_rank_cd` (density-aware ranking) jako alternatywę dla domyślnego `ts_rank` w torze
rzadkim. Zmierzone: brak realnej poprawy nad `ts_rank` na testowanych przypadkach. Zmiana
cofnięta (`git stash drop`) — nie weszła do żadnego commita.

## 4. Warstwa 3: czy tor rzadki (BM25/FTS) w ogóle działa po polsku?

Kluczowe ustalenie sesji: kolumna `SearchVector` (i cały tor rzadki) używa configu
`simple` — **bez lematyzacji**. `to_tsvector('simple', 'rozwiązała')` nie ma nic wspólnego
z `to_tsvector('simple', 'rozwiąże')` — różne formy tego samego słowa to dla `simple` różne,
niepowiązane tokeny. Zmierzone na 9-pytaniowym podzbiorze golden-setu (`InCorpus`,
`expectedEli`+`expectedArticle`), metodologia: `SearchVector @@ websearch_to_tsquery('simple', …)`,
trafienie = właściwy artykuł w wyniku:

**PRZED (simple): 0/9.** Tor rzadki nie trafiał we właściwy artykuł ani razu — potwierdzone
niezależnie w tej sesji i w równoległym przeglądzie (`PRZEGLAD-JAKOSC-I-LATENCJA-2026-08-14.md`,
finding P7).

## 5. Decyzja: dodać prawdziwy polski lematyzator (hunspell), bez ruszania embeddingów

Wymaganie użytkownika: *"tak żeby nie popsuć obecnie dostępnych embedingów"* — embeddingi (tor
gęsty) miały zostać całkowicie nietknięte. Zbudowano niezależnie dwie ścieżki (moją i
równoległego zespołu), porównano, wyprowadzono najlepszy wariant, i wykonano
`docs/RUNBOOK-SLOWNIK-POLSKI-FTS.md` na `.11` (jedyna maszyna z pełnym korpusem, 7,5 mln chunków).

### Co zbudowano (i co ZOSTAŁO w bazie)

- `infra/Dockerfile.db` — obraz Postgres+pgvector z `hunspell-pl` (Debian, ten sam upstream co
  `libreoffice-dictionaries`, licencja permisywna z opcją Apache-2.0).
- `infra/tsearch/polish.stop` — 94-liniowa lista stopwords w **przestrzeni lematów** (nie
  powierzchniowej) — zawiera pułapki typu `cza`→„czy", `luba`/`lubić`→„lub".
- Na `.11`: `CREATE TEXT SEARCH DICTIONARY polish_hunspell`, `CREATE TEXT SEARCH CONFIGURATION
  polish`, oraz **wyrażeniowy indeks GIN** `IX_chunks_SearchVector_pl` na
  `to_tsvector('polish', coalesce("Text",''))` — **2494 MB** (dla porównania stary indeks na
  `simple`: 3011 MB). Budowa: ~6,5h realnie (znacznie dłużej niż referencyjne 15–40 min z
  runbooka równoległego zespołu — niewyjaśniona różnica, prawdopodobnie wzrost korpusu lub I/O
  środowiska; nie badane dalej, bo `statement_timeout=0` gwarantował zakończenie).
- Świadomie **wyrażeniowy indeks, nie przepisanie kolumny `SearchVector`** — przepisanie
  materializowanej kolumny generowanej wymagałoby pełnego rewrite'u tabeli (7,5 mln wierszy),
  ten sam koszt co dzisiejszy build indeksu, tylko na całej tabeli zamiast samego indeksu.

### Zmierzony efekt (Krok 6 runbooka)

| | PRZED (`simple`) | PO (`polish`, sam operator AND) |
|---|---|---|
| trafień we właściwy artykuł (9 pytań) | **0/9** | **2/9** (`kp-52`, `konsument-odstapienie`) |

Potwierdzone jako **czyste porównanie** (nie confound) — `SearchVector` jest wygenerowana
dokładnie jako `to_tsvector('simple', coalesce("Text",''))`, identyczne źródło tekstu, jedyna
zmienna to config.

`IX_chunks_SearchVector_pl` faktycznie używany (`Bitmap Index Scan`, nie `Seq Scan`), zapytania
BEZ rankingu (samo `WHERE ... @@ ...`) szybkie: 14–135 ms. RSS/backend: +29,8 MB (zgodne z
referencją ~31 MB równoległego zespołu).

### Dlaczego tylko 2/9, nie więcej — analiza per przypadek

Sprawdzono realną treść chunków dla 5 nietrafionych pozycji. Dwie odrębne przyczyny:

**a) AND-semantyka rozbija trafienia rozbite na osobne chunki-paragrafy.** `kro-rozwod`
(oczekiwane: KRO art. 56, pytanie *"W jakich okolicznościach sąd może orzec rozwód
małżonków?"*): art. 56 jest w bazie jako 3 chunki (§1/§2/§3). §1 zawiera dokładnie treść
odpowiedzi i dzieli 3 z 5 zlematyzowanych słów pytania (sąd, rozwód, małżonek) — ale
`websearch_to_tsquery` ANDuje wszystkie 5 słów, a dwóch brakujących („okoliczność", „orzec")
nie ma w TYM chunku. Zero trafień mimo bliskości leksykalnej. Podobny wzorzec: `kpk-41`.

**b) Prawdziwe niedopasowanie słownictwa prawnego — nienaprawialne przez FTS.**
`uodo-107` (oczekiwane: UODO art. 107, pytanie o *"wyciek danych... z systemów
informatycznych... medyczne"*): realna treść artykułu to przepis karny za nielegalne
przetwarzanie w ogóle („kto przetwarza dane osobowe... nie jest uprawniony... podlega
grzywnie") — zero słów wspólnych z pytaniem poza „dane"/„osobowy". `uodo-60` (oczekiwane: UODO
art. 60): cała treść artykułu to 54-tokenowy stub definicyjny („postępowanie... prowadzi Prezes
Urzędu") — **a pytanie używa nieaktualnej nazwy organu** („główny inspektor danych osobowych" =
GIODO, zlikwidowany ustawą z 2018 r. na rzecz Prezesa UODO). Żaden lematyzator ani operator nie
zbliży „inspektor" do „Prezes Urzędu" — to nie forma tego samego słowa.

## 6. Próba integracji z kodem (2026-08-15) — PRÓBOWANE, ODRZUCONE, NIE WYSŁANE

Cel: przełączyć tor rzadki/akronimowy `HybridRetriever.cs` z LINQ na `SearchVector`/`simple` na
surowe SQL z `to_tsvector('polish', …)`, żeby faktycznie skorzystać z Kroku 5. Trzy warianty
zaimplementowane i zmierzone na **dokładnym kształcie zapytania produkcyjnego**
(`WITH q AS (...) SELECT ... ts_rank(...) ... ORDER BY "Rank" DESC LIMIT k`), nie na
uproszczonych testach — pierwsza wersja tego pomiaru była błędna metodologicznie (testowała
tylko koszt `WHERE`, nie koszt `ORDER BY ts_rank`) i został to złapane w toku pracy.

| Wariant | Wynik pomiaru | Werdykt |
|---|---|---|
| **AND, bez cappingu** (dokładnie to, co miał wysłać kod) | dla `kp-52` (przypadek realnej poprawy) **nie kończy się w 20s** | ODRZUCONE — brak timeoutu/fail-open, zawiesiłoby produkcję |
| **OR-fallback** (gdy AND=0, `regexp_replace(' & '→' | ')` na tsquery) | 7/7 testowanych pytań (w tym `kro-rozwod`) **timeout nawet przy 90s** | ODRZUCONE — martwy kod, tylko koszt |
| **Dwufazowe** (tani `LIMIT 1000` w kolejności bitmapy, potem `ts_rank` tylko na tych) | szybkie (~3,6s), ALE właściwy artykuł dla `kp-52` **nie przeżywa cappingu** (`f`) | ODRZUCONE — kupuje bezpieczeństwo kosztem całej poprawy jakości |

**Przyczyna źródłowa (wspólna dla wszystkich trzech):** `ts_rank` na wyrażeniowym indeksie GIN
**przelicza `to_tsvector('polish', Text)` od nowa dla każdego pasującego wiersza** — GIN
przechowuje tylko listy postingowe (do samego `WHERE ... @@ ...`), nie zwraca obliczonego
tsvectora do ponownego użycia w `ORDER BY`. Stary kod (`simple`) ranguje po **materializowanej,
przechowywanej kolumnie** `SearchVector` — to odczyt gotowej wartości, praktycznie darmowy.
Zamiana na `polish` bez materializacji zamienia „darmowy ranking" na „hunspell per wiersz przy
KAŻDYM zapytaniu z niemałą liczbą trafień" — i to jest koszt, którego żaden z trzech wariantów
operatora/cappingu nie obchodzi, bo problemem nie jest selektywność WHERE, tylko sam ranking.

**Decyzja:** `HybridRetriever.cs` cofnięty do stanu sprzed eksperymentu (`git checkout`).
Nic z tego nie trafiło do żadnego commita ani nie zostało wypchnięte.

## 7. Stan obecny (2026-08-15)

- **Baza (`.11`):** obiekty `polish_hunspell`/`polish`/`IX_chunks_SearchVector_pl` **istnieją i
  działają poprawnie technicznie** — ale kod aplikacji ich nie używa. Zero wpływu na
  produkcyjne zapytania dziś.
- **Kod:** identyczny jak przed rozpoczęciem prac nad polskim FTS — tor rzadki nadal na
  `simple`, zero lematyzacji, nadal 0/9 na golden-set metodologii z sekcji 4.
- **Jedyna zmiana, która realnie działa na produkcji:** `HnswEfSearch = 1000` (sekcja 2,
  commit `28b50f3`) — tor gęsty.
- Zmiany w bazie są w pełni odwracalne (`docs/RUNBOOK-SLOWNIK-POLSKI-FTS.md`, sekcja 13) i
  addytywne — nie blokują niczego, mogą poczekać.

## 8. Co by rozwiązało problem naprawdę — i ile to kosztuje

Żeby `polish` dawał realną, bezpieczną poprawę, ranking musi być **darmowy jak dziś**, czyli
potrzebna jest **materializowana kolumna** `to_tsvector('polish', …)` (STORED generated column),
nie sam wyrażeniowy indeks. To wymaga:

1. Nowej kolumny + `ALTER TABLE ... ADD COLUMN` ze STORED generated expression — **pełny
   rewrite tabeli, 7,5 mln wierszy** — ten sam rząd wielkości czasu co dzisiejszy build
   samego indeksu (~6,5h zmierzone), prawdopodobnie więcej (rewrite całej tabeli, nie tylko
   indeksu).
2. Nowego indeksu GIN na tej kolumnie (kolejne kilka godzin, jak Krok 5 dziś).
3. Dopiero wtedy przełączenie kodu jest bezpieczne w kształcie identycznym do dzisiejszego
   (`c.SearchVector!.Rank(...)` na nowej kolumnie) — bez żadnego z problemów z sekcji 6.
4. Osobno: nawet z darmowym rankingiem, AND vs OR nadal wymaga decyzji (paragraf-rozbite
   trafienia jak `kro-rozwod`) — ale to już tani, bezpieczny problem do rozwiązania na
   materializowanej kolumnie (ranking nie kosztuje, więc OR nie ma tego samego problemu
   wydajnościowego co dziś).

To osobna, świadoma decyzja infrastrukturalna (kolejne wielogodzinne okno na `.11`, disk space
na dodatkową kolumnę) — nie zrobione w tym przebiegu, czeka na Twoją decyzję.

## 9. Rzeczy nienaprawialne samym FTS (niezależnie od configu/operatora)

Z analizy w sekcji 5b: część przypadków (np. `uodo-107`, `uodo-60`) nie jest problemem
retrievalu leksykalnego w ogóle:

- **Niedopasowanie terminologii prawnej** (synonim, nie forma słowa) — wymaga wnioskowania
  LLM nad dobrze dobranym kontekstem lub słownika synonimów prawnych, nie lematyzacji.
- **Pytania z nieaktualną terminologią** (np. „główny inspektor" zamiast „Prezes UODO") —
  warto rozważyć poprawienie/wymianę takich pozycji w golden-set (`uodo-60` kandyduje).

## 10. Otwarte wątki na przyszłość (nieopdjęte dziś)

- Materializowana kolumna `polish` (sekcja 8) — decyzja + kolejne wielogodzinne okno.
- AND→OR z rankingiem — dopiero sensowne PO materializacji (sekcja 8, punkt 4).
- Rewizja `uodo-60` w golden-set (nieaktualna terminologia w pytaniu).
- Task 6 z `docs/PLAN-ODSIEW-SZUMU-RETRIEVALU.md` (kalibracja marginesu, usunięcie kotwic z
  foldu) — formalnie nadal `in_progress`, nie poruszane w tym wątku.
- `TopK×4 → TopK×8` (sekcja 2) — realnie naprawia RTV/opłaty abonamentowe, ale koszt
  +1,8–2,2s/zapytanie nie zaakceptowany bez dalszej pracy nad batchowaniem rerankera.
