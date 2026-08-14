# Przegląd: jakość odpowiedzi i latencja (2026-08-14)

Zamówienie: przegląd całego projektu pod kątem (a) jakości odpowiedzi — ze szczególnym naciskiem na
czyszczenie danych źródłowych — i (b) latencji odpowiedzi. Zakres przejrzany end-to-end:
ingest/normalizacja → chunking → retrieval (dense/BM25/akronim/tory dokładne/most cytowań) → rerank →
prompt → generacja. Stan kodu: branch `feat/halfvec-retriever` @ `65b0e79` (zsynchronizowany z origin,
126 commitów przed `main`).

Format każdego znaleziska: **co i gdzie → skutek → propozycja → ryzyka**. Sortowanie wg wagi w obrębie
grupy. Przy każdym oznaczenie, czy to fakt z lektury kodu, czy hipoteza wymagająca pomiaru.

Uwaga ogólna: komentarze w tym kodzie mają udokumentowaną proweniencję pomiarową (data + liczba), co
przy tym przeglądzie było głównym narzędziem. W większości znalezisk poniżej problem jest już opisany
we własnym komentarzu autora — zmieniło się to, że (a) korpus wszedł w pełną skalę, albo (b) łatka
trafiła w jeden tor, a nie w oba.

**Status prac (2026-08-14):** grupa A (P1–P6) — **ZROBIONA** w tej sesji, szczegóły w statusach przy
każdym punkcie. Testy: 481/481 zielone (było 469 przed sesją; +12 nowych). Grupa B (P7 słownik `polish`,
P8 re-chunking aktów, P9 odsiew u źródła) wymaga przebudowy indeksu / reprocessingu korpusu → najpierw
dyskusja i pomiar na próbce.

Weryfikacja środowiskowa: lokalny Postgres (`infra/compose.yaml`, usługa `db`) — to na nim wyszło, że
migracje produkują ZŁY indeks (P1). Baza dev miała 39 orzeczeń i zero aktów, więc pomiary latencji
w skali pełnego korpusu (7,4 mln chunków) **nie były tu możliwe** — zmiany latencyjne (P3–P6) są
uzasadnione strukturalnie (mniej kolumn, mniej zapytań, krótsza transakcja) i dowiedzione brakiem
regresji, ale ich EFEKT trzeba zmierzyć na maszynie z 3060.

---

## Grupa A — ZROBIONE (bez dotykania indeksu i embeddingów)

### P1. Indeks HNSW `halfvec` nie istnieje w migracjach — tylko w żywej bazie
**Status: ZROBIONE.** Migracja `20260814104015_SyncHalfvecEmbeddingIndex` (warunkowy DO-block: no-op, gdy
indeks już jest wyrażeniowy — na prodzie ZERO przebudowy 18 GB; podmiana, gdy jest w postaci fp32),
usunięcie deklaracji `HasIndex(x => x.Embedding)` z modelu EF (EF nie umie wyrazić rzutu typu),
poprawka `RUNBOOK-3060-DOCKER.md` (krok 9 tworzył wariant fp32), test `HalfvecIndexTests`.
Dowód empiryczny (plany zapytań w izolowanej bazie, `enable_seqscan = off`): zapytanie fp32 na indeksie
fp32 → `Index Scan`; zapytanie PRODUKCYJNE (rzut na halfvec) na indeksie fp32 → **`Sort + Seq Scan`**;
to samo zapytanie na indeksie wyrażeniowym → `Index Scan`. Idempotencja sprawdzona osobno: powtórne
wykonanie DDL na bazie z halfvec wypisuje „pomijam" i nie tyka indeksu.

**Fakt (lektura kodu + runbooki).** Zapytanie gęste rzutuje obie strony na `halfvec(1024)` i **wymaga
indeksu wyrażeniowego** na `Embedding::halfvec(1024)` (`HybridRetriever.cs:328`). Tymczasem:
- model EF deklaruje `hnsw ("Embedding" vector_cosine_ops)` na fp32 (`PrawoRagDbContext.cs:79`),
- `docs/RUNBOOK-3060-DOCKER.md:201` dokumentuje tę samą postać fp32,
- **żadna migracja nie zawiera słowa `halfvec`** — indeks żyje wyłącznie dlatego, że został zbudowany
  ręcznie (`docs/SESJA-2026-07-17-HALFVEC-EVAL-DIAGNOZA-CZATU.md:26`, 18 GB, trzy podejścia).

**Skutek:** środowisko odtworzone z migracji (nowa maszyna, odtworzenie po awarii, CI na pełnym dumpie)
dostaje tor gęsty robiący seq scan po 7,4M wierszy — różnica między ~100 ms i minutami, **bez żadnego
sygnału błędu**. Dodatkowo dwie różne definicje walczą o tę samą nazwę `IX_chunks_Embedding`.

**Propozycja:** migracja z surowym SQL tworzącym indeks wyrażeniowy; usunąć/przemianować deklarację
`HasIndex(x => x.Embedding)` w modelu EF, żeby nazwy nie kolidowały; poprawić runbook.

**Ryzyka:** `CREATE INDEX` na 7,4M to godziny i 18 GB — migracja musi być bezpieczna do uruchomienia
na żywej bazie, gdzie indeks JUŻ istnieje (`IF NOT EXISTS`) i nie może go przebudować przy okazji.
Rozważyć `CONCURRENTLY` (poza transakcją migracji) albo świadomie zostawić budowę indeksu runbookowi,
a w migracji tylko zsynchronizować model EF — decyzja do podjęcia przy implementacji.

### P2. Bramka abstynencji nie widzi torów dokładnych
**Status: ZROBIONE (część 1 z 2).** Nowy, TRZECI rozdzielony sygnał `RetrievalResult.ExactMatchHits`
(liczony po liście FINALNEJ, więc uwzględnia cap per dokument i TopK), a `AbstentionPolicy` nie odmawia,
gdy jest ≥1 trafienie dokładne. Most cytowań świadomie NIE podnosi tego sygnału (pochodny, nie jawny
ask) — pilnuje tego osobny test. Skala i semantyka `MaxSimilarity` nietknięte, więc próg 0,55 zostaje
kalibrowany na tym samym sygnale co dotąd. Testy: 4 nowe w `AbstentionPolicyTests` + dowód end-to-end
w `SignatureLaneTests` (retriever raportuje sygnał, bramka przepuszcza) + kontrola negatywna (pytanie
bez sygnatury → `ExactMatchHits == 0`, zero rozluźnienia dla pytań opisowych).
**Część 2 NIEZROBIONA świadomie:** liczenie `MaxSimilarity` po chunkach faktycznie dostarczonych (dziś
to maksimum po puli 50 kandydatów) zmienia znaczenie progu, więc wymaga rekalibracji na golden secie —
osobne zadanie, nie skutek uboczny.

**Fakt (lektura kodu).** `AbstentionPolicy.cs:12` czyta `result.MaxSimilarity`, a to jest maksimum cosine
**wyłącznie z toru gęstego**, po całej puli `CandidatesPerPath=50` (`HybridRetriever.cs:156`). Tory
dokładne ustawiają `Similarity = null` (`:375` sygnatura, `:418` akt, `:481` artykuł/most).

**Skutek — dwa osobne:**
1. Pytanie „III SA/Po 154/26": lane sygnatury pobiera DOKŁADNIE to orzeczenie, ale goła sygnatura
   embeduje się bezwartościowo → `MaxSimilarity` niskie → **system odmawia, mając odpowiedź w ręku**.
   To samo dla „Dz.U. 2025 poz. 1815" (lane aktu). Tory zbudowano właśnie na te przypadki, a bramka
   je unieważnia.
2. Bramka ocenia pulę 50 kandydatów, z której większość jest wyrzucana — próg mierzy inny zbiór niż
   ten, który faktycznie dostaje model (finalne `TopK` może być w całości z torów dokładnych/mostu).

Metryką nadrzędną projektu jest odsetek odmów na realnych pytaniach, więc to najwyższa dźwignia
jakościowa przy minimalnej zmianie.

**Propozycja:** nie odmawiać, gdy którykolwiek tor dokładny zwrócił trafienie (sygnał niesiony obok
`MaxSimilarity`, nie przez nadpisanie go — ta sama zasada, dla której `RerankTopScore` jest osobnym
polem); rozważyć dodatkowo liczenie `MaxSimilarity` po chunkach FAKTYCZNIE dostarczonych.

**Ryzyka:** rozluźnienie bramki może podnieść odsetek odpowiedzi na pytania bez pokrycia (odwrotna
strona metryki) — dowodzić `--refusals` + golden setem przed/po, a nie samym rozumowaniem. Zmiana
progu bez zmiany skali jest bezpieczna; wprowadzenie DRUGIEGO sygnału do bramki wymaga kalibracji.

### P3. Transakcja trzymana otwarta przez wywołania cross-encodera
**Status: ZROBIONE.** Transakcja obejmuje teraz WYŁĄCZNIE tor gęsty (tylko on potrzebuje
`SET LOCAL hnsw.ef_search`), z jawnym commitem. Efekt uboczny, który UPROŚCIŁ kod: savepointy w torze
akronimowym przestały być potrzebne — istniały tylko dlatego, że tor biegł wewnątrz transakcji
obejmującej cały retrieval, a Postgres po błędzie zatruwa całą otaczającą transakcję (25P02). Bez
transakcji każde zapytanie jest własną, niejawną transakcją, więc `try/catch` wystarcza; fail-open toru
akronimowego zachowany.

**Fakt (lektura kodu).** `HybridRetriever.cs:45` otwiera transakcję (potrzebną wyłącznie po to, żeby
`SET LOCAL hnsw.ef_search` obowiązywał na tym samym połączeniu co zapytanie dense — `:44-46`), a `tx`
żyje do końca metody, czyli **przez oba round-tripy HTTP do rerankera** (`:169`, `:222`) oraz wszystkie
tory dokładne i most.

**Skutek:** połączenie wisi `idle in transaction` przez sekundy na każde pytanie — zjada pulę połączeń
przy równoległych użytkownikach i blokuje vacuum na `chunks`/`documents`.

**Propozycja:** zawęzić transakcję do samego toru gęstego (`SET LOCAL` + `DenseAsync`), reszta poza nią.

**Ryzyka:** niskie; uwaga na `AcronymLaneTimeout` i savepointy (`:97-114`) — savepoint wymaga
transakcji, więc tor akronimowy musi albo dostać własną, albo zrezygnować z savepointu na rzecz
osobnego połączenia. Test regresji: te same wyniki dla zapytania z akronimem i bez.

### P4. Hot path ciągnie embeddingi i tsvectory, których nie używa
**Status: ZROBIONE.** Wszystkie pobrania chunków w retrieverze i augmenterze idą przez projekcje
(`ChunkRow`, `AmendmentChunkRow`) zamiast pełnych, śledzonych encji — bez `Embedding` i `SearchVector`.
Projekcja do typu innego niż encja wyłącza śledzenie z definicji, więc `AsNoTracking` jest zbędne.
Przy okazji trzy identyczne bloki konstruujące chunk toru dokładnego zwinięte w jeden mapper
(`ExactMatchChunk`) — zachowanie 1:1, w tym marker `Score = double.MaxValue`.
W augmenterze świadomie BEZ `Take` na chunkach noweli: dopasowanie idzie po treści diffu, więc obcięcie
listy zmieniałoby WYNIKI, a nie tylko koszt — zawężamy szerokość wiersza, nie liczbę wierszy.

**Fakt (lektura kodu).** Wszystkie zapytania retrievera pobierają **pełne, śledzone encje**
`ChunkEntity`, a ta ma `Embedding` (1024×fp32 ≈ 4 KB) i `SearchVector` (tsvector długiego chunka to
kolejne kilka KB) — `ChunkEntity.cs:25,38`:
- `HybridRetriever.cs:130` — `TopK*4` = 32 kandydatów z `Include(Document)`,
- `:360` — 12 chunków (sygnatura), `:403` — 15 (akt), `:467` — do 20 × 4 cytaty (artykuł/most),
- `TemporalAugmenter.cs:74` — **wszystkie** chunki noweli, bez `Take`.

Zero `AsNoTracking`, zero projekcji — podczas gdy cały `PrawoRAG.Eval` używa `AsNoTracking`
konsekwentnie (`ChunkProbe.cs:128,138,198`, `ActLaneProbe.cs:91,152`, `ExamRunner.cs:162`).

**Skutek:** setki KB wektorów przez sieć na turę czatu + snapshoty change trackera, dla danych,
których nikt nie czyta. Najtańszy mierzalny zysk latencji w repo.

**Propozycja:** projekcja do DTO (Id, Text, Section, ArticleNo, Locator, ChunkIndex + potrzebne pola
dokumentu) albo minimum `AsNoTracking()` + `Select`.

**Ryzyka:** niskie — `RetrievedChunk` już jest DTO, więc zmiana jest lokalna w retrieverze/augmenterze.
Uwaga na `TemporalAugmenter.cs:74`, gdzie `ch` idzie do `ToAmendmentChunk` (potrzebuje `Document.*`).

### P5. Rozpoznanie aktu = seq scan, do 6× na pytanie (12× na follow-upie)
**Status: ZROBIONE (wariant per-request).** Memoizacja w instancji retrievera (`AddScoped` → zasięg
jednego żądania, obejmuje OBA retrievale follow-upu). Klucz `Ordinal`, celowo czuły na wielkość liter:
`ILIKE` jest nieczuły, ale `similarity()` nie — wspólny wpis dla „KC" i „kc" mógłby zwrócić wynik,
którego baza dla danego zapisu nie dałaby. Cache PROCESOWY (zero zapytań po rozgrzaniu) NIEZROBIONY
świadomie: zapamiętany NULL trwale ukrywałby akt dodany później, więc wymaga unieważniania po ingeście.
Testy: `ActResolutionCacheTests` — drugi cytat tego samego aktu dalej dociąga swój artykuł (ścieżka
trafienia w cache) i powtórzony retrieval na tej samej instancji daje identyczny wynik.

**Fakt (lektura kodu + migracje).** `ResolveActAsync` (`HybridRetriever.cs:488`): gałąź aliasu robi
`ILIKE '%…%'` + sortowanie po długości tytułu; gałąź frazy liczy `TrigramsSimilarity` **dla każdego
aktu w korpusie**, bez prefiltru operatorem `%` i bez indeksu — migracja `20260707151013:26` zakłada
rozszerzenie `pg_trgm`, ale **indeksu GIN na `documents.Title` nigdy nie utworzono**.
Wywołań na retrieval: do 4 z `StructuralAsync` (`:453`) + do 2 z `CitationBridgeAsync` (`:300`).

**Propozycja:** cache w pamięci `alias/fraza → ExternalId` (zbiór aktów jest mały i statyczny w obrębie
procesu) — koszt spada do zera. Jeśli ma zostać w SQL: `CREATE INDEX … USING gin ("Title" gin_trgm_ops)`
plus `Title % hint` jako prefiltr przed sortowaniem po podobieństwie.

**Ryzyka:** cache musi być unieważnialny po ingeście nowych aktów (albo per-request memoizacja, co i tak
zbiera 6→1 wywołanie w obrębie jednego pytania — wariant bez ryzyka, bez inwalidacji, warty rozważenia
jako pierwszy krok).

### P6. `TemporalAugmenter` skanuje metadane wszystkich aktów na każde pytanie
**Status: ZROBIONE (częściowo).** Skan zawężony filtrem `JsonExists` (operator jsonb `?`) do aktów, które
FAKTYCZNIE mają klucz `unabsorbedAmendments` — zamiast ściągać duże jsonb każdego aktu, żeby zwykle nie
znaleźć nic. Dwa zapytania o ten sam zbiór dokumentów scalone w jedno z projekcją.
**Zostaje:** to nadal skan tabeli `documents` (brak indeksu na `DocType`, brak GIN na `TypedMetadata`).
Cache procesowy albo indeks częściowy zdjąłby resztę — pierwszy wymaga unieważniania, drugi migracji na
523k dokumentów, więc do zrobienia z pomiarem na pełnym korpusie.

**Znalezisko poboczne, ważniejsze niż sama optymalizacja:** prawdziwy `TemporalAugmenter` miał ZERO
pokrycia testami (wszystkie testy używały atrap `NoOpAugmenter`), a każdy wywołujący owija go
w `try { … } catch { /* best-effort */ }`. Czyli dowolny błąd w środku — nieprzetłumaczalne LINQ, zmiana
kształtu metadanych, literówka w kluczu jsonb — był CICHY: oznaczanie i dokładanie nowel przestawało
działać, a odpowiedź wyglądała normalnie. Powstał `TemporalAugmenterTests` (żywa baza, kształt metadanych
mirrorujący produkcję: PascalCase `EliId`/`EffectiveDate`, bo `ParseUnabsorbed` czyta case-sensitive):
dokładanie fragmentu noweli dla pytanego artykułu, oznaczanie chunka, którego własny dokument jest
nowelą (AKT-4b), i zwrot wejścia bez zmian, gdy w wynikach nie ma aktów.

**Fakt (własny komentarz autora + skala korpusu).** `TemporalAugmenter.cs:105`
(`BuildUnabsorbedDatesAsync`) czyta `TypedMetadata` **wszystkich** dokumentów typu `act` przy każdej
turze, która zwróciła choć jeden chunk aktu. Komentarz `:32-35` mówi wprost: „tanie przy dzisiejszej
skali korpusu ~40 aktów; przy »pełnym korpusie« v1 wymagałoby indeksu/cache — poza zakresem teraz".
Pełny ISAP jest już zembedowany → dług jest aktywny. Dodatkowo `:37` i `:53` pytają o ten sam zbiór
dokumentów dwa razy, drugi raz jako pełne śledzone encje.

**Propozycja:** cache słownika `ExternalId → EffectiveDate` (unieważniany po ingeście aktów) albo
zawężenie zapytania do dokumentów, które faktycznie mają `unabsorbedAmendments` (indeks GIN na jsonb
albo kolumna wyliczana `HasUnabsorbed`); scalić `:37` i `:53` w jedno zapytanie z projekcją.

**Ryzyka:** niskie; `AugmentAsync` jest już best-effort w callerze (`ChatService.cs:54`), więc awaria
nie blokuje odpowiedzi.

---

## Grupa B — do dyskusji (wymaga przebudowy indeksu lub reprocessingu)

### P7. Tor BM25 jest prawdopodobnie martwy na pytaniach opisowych
**Hipoteza wymagająca pomiaru — mocna, ale NIEZMIERZONA.** `PrawoRagDbContext.cs:19` ustawia
`TextSearchConfig = "simple"` z komentarzem „przełączyć po weryfikacji (0.2/3.1)". W połączeniu
z `WebSearchToTsQuery` (`HybridRetriever.cs:55`) daje to trzy nakładające się problemy:
- **brak stemmingu** — „przedawnienie roszczenia" nie dopasuje „przedawnienia roszczeń" (w polskim to
  nie corner case, to norma),
- **brak stoplisty** — `simple` indeksuje „czy", „może", „w",
- **`websearch_to_tsquery` AND-uje wszystkie tokeny**.

Czyli pytanie „czy pracodawca może nagrywać rozmowy pracownika" wymaga WSZYSTKICH form powierzchniowych
w jednym chunku. Wniosek: tor rzadki odpala realnie tylko na krótkich zapytaniach kluczowych
i sygnaturach, a na typowym pytaniu „hybrydowy" retriever jest dense-only i fuzja RRF miesza jeden
ranking z pustką.

To spójne z historią łatek: tor akronimowy (`:72-121`) obchodzi dokładnie tę dziurę („websearch AND-uje
wszystkie słowa pytania, więc chunki zawierające akronim, ale nie resztę słów, wypadały z toru
rzadkiego" — komentarz `:73-75`), a `HnswEfSearch=400` kompensuje recall, który normalnie dałby tor
leksykalny.

**Pomiar rozstrzygający (tani, bez zmian w bazie):** przebieg golden setu z WYŁĄCZONYM torem rzadkim.
Brak ruchu metryki = dowód, że tor jest dziś martwym balastem, i twardy priorytet naprawy.

**Propozycja (po pomiarze):** słownik `polish` (ispell/hunspell w obrazie) — wymaga przebudowy kolumny
generowanej i indeksu GIN na 7,4M (jednorazowo, godziny, BEZ re-embeddingu). Wariant bez zależności od
słownika: `plainto_tsquery` z semantyką OR + `ts_rank`.

**Ryzyka:** zmiana konfiguracji tsvector zmienia zachowanie WSZYSTKICH torów leksykalnych naraz
(rzadki + akronimowy) — kalibracja RRF i progów może wymagać powtórzenia; `simple` jest w stockowym
obrazie, `polish` dokłada zależność do runbooka i Testcontainers.

### P8. Akty tracą kontekst przy długich artykułach (re-chunking)
**Fakt (lektura kodu).** Trzy rzeczy składają się na jeden problem:
1. `ActTextParser.Clean` (`:121`) skleja WSZYSTKIE białe znaki, w tym `\n` → treść artykułu to jedna
   linia.
2. `TokenAwareChunker.SplitUnits` (`:84`) dzieli jednostki po `\n` → dla aktów nie ma na czym pracować,
   więc długi artykuł wpada w `EnsureWithinMaxAsync`/`SplitInHalf` (`:104-134`) i jest **dzielony na
   pół po najbliższej spacji, rekurencyjnie** (cięcie w środku zdania/wyliczenia).
3. Nagłówek kontekstowy „⟨tytuł⟩, Art. N" jest w `ActTextParser.Emit` osobną linią przed treścią
   (`:97`), czyli osobną jednostką packingu → **trafia tylko do pierwszej połówki**.

**Skutek:** połówki 2..n długiego artykułu siedzą w indeksie jako bezimienny tekst — gorszy embedding,
gorsze cytowanie, i to dokładnie na typie dokumentu, na którym najbardziej zależy (norma przed
narracją — `GroundedPrompt.cs:92`, diagnoza 2026-07-17).

**Propozycja:** dzielenie po `§`/punktach (nie po połowie długości) + re-prefiksowanie nagłówka
kontekstowego na KAŻDYM chunku artykułu.

**Ryzyka:** wymaga reprocessingu i re-embeddingu aktów (nie całego korpusu — orzeczeń nie dotyka).
Zmienia granulację, więc unieważnia kalibracje robione na dzisiejszych chunkach aktów. Najpierw pomiar
na próbce jednego kodeksu, potem decyzja.

### P9. Reguły odsiewu degeneratów nie weszły do ingestu
**Fakt (lektura kodu).** `ChunkDegeneracy` jest używane WYŁĄCZNIE przez `SanitizeChunksRunner`, który
działa post factum, zerując `Embedding` (`SanitizeChunksRunner.cs:18`, komentarz `:53`: „te same reguły
wejdą do chunkera w JAK-2"). `TokenAwareChunker` sprawdza tylko `MinSubstantiveWords` (`:52`).

**Skutek — trzy wycieki tych samych śmieci:**
1. tor BM25 ich nie filtruje (świadomie, `SanitizeChunksRunner.cs:21-22`),
2. **tory dokładne ich nie filtrują** — `FetchArticleAsync` (`:467`) i `SignatureAsync` (`:360`) nie
   mają warunku `Embedding != null`, więc na „co mówi art. X" może wrócić zsanityzowany placeholder
   „(uchylony)",
3. każdy reprocess/re-embedding wskrzesza je w całości (`Embedding IS NULL` to sygnał „do
   zembedowania").

**Propozycja:** detektor do chunkera (odsiew u źródła). Łatka natychmiastowa, mieszcząca się w grupie A:
warunek `Embedding != null` w torach dokładnych — zamyka wyciek (2) bez reprocessingu.

**Ryzyka:** odsiew u źródła = reprocessing; łatka na tory dokładne = zerowe ryzyko, ale nie usuwa
przyczyny.

---

## Znaleziska pomniejsze (do zrobienia przy okazji dotykania tych plików)

### Z1. Komórki tabel zlepiają się przy HTML→tekst
`HtmlText.cs:10-11` — `td`/`th` nie są w `BlockTags`, więc wiersz „100 zł | 200 zł" wychodzi jako
„100 zł200 zł". W orzecznictwie podatkowym i administracyjnym tabele są częste. Jedna linia poprawki.

### Z2. Follow-up = dwa pełne retrievale sekwencyjnie
`FollowUpSelector.cs:29` i `:41`. Każdy to embedding zapytania + HNSW z `ef_search=400` + BM25 + tory
akronimowe + do 6 skanów rozpoznania aktu + 2 wywołania cross-encodera. Komentarz „SEKWENCYJNIE —
wspólny scoped DbContext nie jest thread-safe" jest słuszny, ale to argument za `IDbContextFactory`,
nie za sekwencyjnością — dwa warianty na osobnych połączeniach skracają latencję follow-upu prawie
o połowę. Tańszy wariant: bramka licząca wariant kontekstowy tylko dla pytań faktycznie anaforycznych
(krótkie, zaimek, „a co z…"). **Uwaga:** to zmienia semantykę wyboru wariantu, więc wymaga
`--refusals` przed/po — dlatego NIE w grupie A.

### Z3. Asymetria filtra REGULATION między torami — do potwierdzenia testem
Tor gęsty używa `IS DISTINCT FROM` z wyraźnym komentarzem o NULL-ach (`HybridRetriever.cs:323-324`),
tor rzadki `!=` przez `GetProperty(...).GetString()` (`:518-519`). EF prawdopodobnie kompensuje to
null-semantyką i generuje równoważny SQL — ale nikt tego nie sprawdził, a jeśli nie kompensuje, to
każde orzeczenie bez klucza `judgmentType` **znika z BM25, zostając w dense**. Jeden test parytetu
zbiorów obu torów zamyka temat (spójne z propozycją #3 z `PRZEGLAD-ZLOZONOSCI-2026-07-19.md`).
Osobno: `GetProperty` rzuca `KeyNotFoundException`, jeśli to wyrażenie kiedykolwiek policzy się po
stronie klienta.

### Z4. `Truncate = true` w rerankerze może obcinać ogony chunków
`TeiReranker.cs:30`. Przy `TargetTokens=450` (`ChunkerOptions.cs:8`) plus zapytanie jesteśmy na granicy
512 tokenów typowego cross-encodera → ogon chunka może być dla sędziego niewidoczny. Potwierdzić
`max_length` modelu z `RerankerOptions.ModelId`.

### Z5. `MinChunkTokens=20` a krótkie normy
`Program.cs:344`. Odcina krótkie jednostki z torów semantycznych; tory dokładne omijają go świadomie
(`:445`, `:463`). Skoro `MinSubstantiveWords=5` odsiewa degeneraty już na ingest, ten filtr może być
dziś redundantny i kosztować recall na krótkich `§` — do zmierzenia, nie do zmiany w ciemno.

### Z7. Jedna wskazówka aktu na CAŁE pytanie — cytaty dwóch kodeksów trafiają w jeden
**Fakt (lektura kodu + potwierdzone na żywej bazie).** Znalezione przy pisaniu testów do P5, nie przy
przeglądzie. `CitationParser.Parse` (`:70`) wywołuje `ActHint(text)` RAZ dla całego pytania i przypisuje
tę samą wskazówkę WSZYSTKIM znalezionym artykułom (`:76`). `ActHint` zwraca pierwszy dopasowany skrót
z listy `Abbrevs` w jej kolejności, nie ten stojący obok danego artykułu.

**Skutek:** pytanie „jak się ma art. 415 KC do art. 52 KP?" rozwiązuje OBA artykuły względem KC —
art. 52 KP nie wchodzi torem strukturalnym (potwierdzone: `ExactMatchHits == 1`, a nie 2; drugi artykuł
wszedł tylko semantyką, więc jego obecność jest kwestią szczęścia w rankingu). Pytania porównawcze
(„czym różni się X w KC od Y w KP") to naturalna klasa pytań prawniczych, więc to realna luka, nie
teoretyczna.

**Propozycja:** wiązać wskazówkę z NAJBLIŻSZYM artykułem (skrót/fraza po numerze, z fallbackiem na
wskazówkę globalną, gdy przy artykule nic nie stoi) — zamiast jednej wskazówki na tekst. Czysta funkcja,
w pełni testowalna bez bazy.

**Ryzyka:** `CitationParser` jest wejściem do toru strukturalnego, mostu i `ExactMatchText` follow-upu —
zmiana dotyka wielu ścieżek naraz, więc wymaga golden setu przed/po. Osobne zadanie, świadomie
NIEZROBIONE w tej sesji (poza zakresem P1–P6).

### Z6. Dryf offsetów lokalizatora
`JudgmentNormalizer.StripFormularzBoilerplate` działa PO pocięciu na sekcje, więc `CharStart`
uzasadnienia przestaje wskazywać na tekst źródłowy; podobnie `TokenAwareChunker.Pack` liczy
`CharEnd = LocalStart + Text.Length` (`:58-60`) z długości sklejonego tekstu, która po zwinięciu białych
znaków nie odpowiada rozpiętości w oryginale. Nie boli, dopóki nie podświetlamy fragmentu w źródle.

---

## Co dalej

Grupa A (P1–P6) jest zrobiona i nie ruszała indeksu ani embeddingów. Do zamknięcia tematu zostaje:

1. **Pomiary na maszynie z 3060** — lokalnie nie było czego mierzyć (39 dokumentów). Trzy rzeczy warte
   liczby przed/po: latencja tury czatu (P3–P6), latencja follow-upu (dwa retrievale) i `--refusals`
   dla P2 (czy odsetek odmów spadł tam, gdzie miał: pytania z sygnaturą/numerem Dz.U./cytatem).
2. **P7 — POMIAR toru rzadkiego przed decyzją o słowniku**: golden set z wyłączonym torem BM25. Brak
   ruchu metryki = dowód, że tor jest dziś martwy, i twardy priorytet.
3. **P8/P9** (re-chunking aktów + odsiew degeneratów u źródła) — największa jakość po stronie danych,
   ale wymaga reprocessingu → najpierw próbka jednego kodeksu, potem decyzja.
4. **Drobne, bez ryzyka, do zrobienia przy okazji**: Z1 (komórki tabel), P2 część 2 (`MaxSimilarity`
   po chunkach dostarczonych — z rekalibracją progu), Z3 (test parytetu filtra REGULATION), Z7
   (wskazówka aktu per artykuł).

Uwaga o kolejności, która wyszła w tej sesji: P1 był priorytetem nie dlatego, że najbardziej boli DZIŚ
(na prodzie indeks jest poprawny, bo zbudowano go ręcznie), a dlatego, że jest niewidoczny — awaria
ujawnia się dopiero przy odtworzeniu środowiska i wygląda jak „RAG jest wolny", nie jak błąd. Ta sama
klasa problemu co brak testów augmentera pod połkniętym wyjątkiem (P6): rzeczy, które psują się CICHO,
są warte więcej uwagi niż rzeczy, które psują się głośno.
