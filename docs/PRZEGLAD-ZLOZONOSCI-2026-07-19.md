# Przegląd złożoności repo (2026-07-19) — opis, propozycje uproszczeń, ryzyka refactoru

Zamówienie: wskazać miejsca, gdzie logika jest przekombinowana — BEZ poprawiania. Każde znalezisko:
co i gdzie → propozycja → ryzyka. Posortowane wg wagi. Uwaga ogólna: właśnie zbudowaliśmy siatkę
bezpieczeństwa (eval odmów, golden set, 250+ testów) — refactory z grupy P1 robić DOPIERO po
zamknięciu fazy JAK, bo wtedy każdą zmianę da się zmierzyć przed/po.

---

## P1 — rdzeń produktu (największa wartość, wymagają siatki pomiarowej)

### 1. Pipeline czatu istnieje w CZTERECH kopiach
`Program.cs:173-265` (SSE `/api/chat`), `ChatService.cs` (Blazor), `RefusalEvalRunner.ReplayAsync`
(eval odmów), `Eval/Program.cs` ścieżka `--chat` (golden set). Wszystkie powielają sekwencję:
retrieval → follow-up 2× z marginesem → bramka abstynencji → augmenter → OrderForGrounding →
Build → LLM → CitationValidator. Komentarze same przyznają problem („parytet z ChatService").
**Rozjazd już nastąpił:** `/api/chat` nie ma obsługi załącznika (DocumentContext), a eval miał
utajony bug ctx z surowych chunków (naprawiony przy DOC).
**Propozycja:** wydzielić `GroundedAnswerPipeline` do projektu widocznego dla Api i Eval
(np. PrawoRAG.Llm albo nowy PrawoRAG.Chat) zwracający strumień zdarzeń domenowych; Api adaptuje
do SSE/Blazor eventów, Eval konsumuje wprost. Poza pipeline'em zostają sprawy transportowe
(auth, CostGuard.Record, nagłówki SSE).
**Ryzyka:** typy zdarzeń (`ChatSource` itd.) żyją w Api i są serializowane do `RetrievedSources`
(jsonb) oraz SSE — przenosiny wymagają zachowania kształtu JSON (test kontraktowy). Parytet
behawioralny dowieść golden setem + `--refusals` przed/po (właśnie po to są). Duży PR — robić
jako osobne zadanie, nie przy okazji.

### 2. Trójstronny niepisany kontrakt: OrderForGrounding ↔ Build ↔ kontekst walidatora
Caller musi (a) zawołać `OrderForGrounding`, (b) podać TĘ SAMĄ listę do `Build`, (c) zbudować
kontekst walidatora TYM SAMYM formatem — format `$"[{i+1}] {LocatorLabel(c)}\n{c.Text}"` jest
skopiowany w 4 plikach (Program.cs:251, ChatService.cs:99, Eval/Program.cs:135,
RefusalEvalRunner.cs:166). Każde nowe miejsce wywołania może się rozjechać (eval już się rozjechał).
**Propozycja:** `Build` zwraca pakiet `GroundingPackage { Request, Sources, ContextTexts,
OrderedChunks }` — porządkowanie i format ctx stają się wewnętrzną sprawą jednej funkcji.
**Ryzyka:** niskie (czysta funkcja, testy Grounding pilnują); churn sygnatur w 4 miejscach.
Naturalnie wchłonięte przez #1 — jeśli robimy #1, #2 robi się „po drodze".

### 3. Semantyka filtrów retrievalu w dwóch językach
`HybridRetriever.ApplyFilters` (LINQ, tor rzadki) i warunki w `DenseAsync` (surowy SQL, tor gęsty)
implementują te same filtry (CourtType/DateFrom/To/OnlyInForce/MinChunkTokens) niezależnie —
zmiana jednego bez drugiego = tory widzą różne korpusy (trudny do zauważenia bug klasy Case 5).
**Propozycja:** minimalna — test parytetu na żywym PG (każdy filtr odsiewa identycznie w obu
torach); pełna unifikacja (generowanie obu z jednej definicji) NIE jest warta złożoności.
**Ryzyka:** wariant testowy — zerowe.

## P2 — ingestia (duplikacje realne, ale kod stabilny; robić przy następnym zadaniu ingestowym)

### 4. Trzy runnery ingestii powielają szkielet
`IngestionRunner` / `RawProcessRunner` / `RawFetchRunner`: identyczny switch po `IngestOutcome`,
rozwiązanie konektora, `CheckpointAsync` (z tym samym komentarzem), `LogProgress` co 50, rekordy
`*Summary`. Tryb `stream` (IngestionRunner) to okrojony RawProcessRunner bez fast-skip/bezpiecznika.
**Propozycja:** zamiast refactoru szkieletu — rozważyć **usunięcie trybu `stream`** (dwufazowość
jest domyślna i lepsza pod każdym względem; ODP-0..4 działa tylko w dwufazowym); potem wspólny
helper checkpoint/liczników dla dwóch pozostałych.
**Ryzyka:** `stream` może żyć w runbookach/nawykach (SESJA.md go wymienia) — najpierw deprecation
z komunikatem, usunięcie po jednym cyklu. Reszta: niskie (testy fetch/process istnieją).

### 5. Format cytatu prawnego budowany w DWÓCH miejscach
`ActNormalizer.Emit` (ścieżka HTML) i `ActTextParser.Emit` (ścieżka PDF) osobno budują etykietę
„Art. N § M pkt K" + próg wstępu `>= 20` powtórzony 3×.
**Propozycja:** wspólny builder etykiety (`CitationLabel.Build(article, paragraph, point)`) +
nazwana stała progu.
**Ryzyko KLUCZOWE:** etykiety wchodzą do TEKSTU chunków → zmiana choćby spacji zmienia ContentHash
/treść i przy reprocessingu przechunkowuje korpus (re-embedding!). Refactor musi być bajt w bajt
równoważny — dowieść testem porównawczym (stare vs nowe na fixture), nie założeniem.

### 6. Helpery JSON przepisane w 5+ plikach
`StringProp`/`StringArray` identyczne w ActNormalizer i JudgmentNormalizer; ten sam wzorzec
w EliSejmConnector, AmendmentRelink, ConversationStore.
**Propozycja:** jeden `JsonProps` (Domain lub Ingestion). **Ryzyka:** znikome; uważać na drobne
różnice wariantów (StringAt) — ujednolicić świadomie, nie mechanicznie.

### 7. Konfiguracja HttpClient/resilience zduplikowana (SAOS vs ELI)
Ten sam blok `AddStandardResilienceHandler` z magią 45 s/30 s/×2 dwa razy.
**Propozycja:** wspólna metoda rozszerzająca z nazwanymi stałymi. **Ryzyka:** znikome.

### 8. `ActNormalizer` — duplikacje wewnętrzne
XPath klasy jednostki powtórzony 6×; `ArticleNumber`/`ParagraphNumber`/`PointNumber` i para
`Strip*Heading` to jeden algorytm sparametryzowany prefiksem/regexem.
**Propozycja:** helper `UnitXPath(klasa)` + jedna metoda z parametrem. **Ryzyka:** średnio małe —
regexy subtelne, ale T-NORM/T-ACT pilnują; obowiązuje to samo ryzyko co #5 (treść chunków).

### 9. Wymuszanie UTC w 3 miejscach z tym samym komentarzem
Konektor, normalizer i pipeline każde po swojemu konwertują daty do UTC („obrona przed" sobą
nawzajem). **Propozycja:** jedna granica konwersji — pipeline przy zapisie; reszta bez konwersji.
**Ryzyka:** pominięta ścieżka = wyjątek Npgsql w runtime; po zmianie przejść wszystkie zapisy dat
(grep po DateTimeOffset w encjach) + test na nie-UTC wejściu.

### 10. Drobiazgi ingestii
`Ingestion/Program.cs`: sekwencja fetch→process skopiowana między trybami `fetch-process`
i `sync-eli` (wydzielić lokalną funkcję — trywialne). `IngestionPipeline`: przekazywanie etapu
przez callback `Action<string> setStage` zamiast wyjątku niosącego etap — okrężne; refactor niskie
ryzyko, ale zachować dosłowny format komunikatów diagnostycznych (testy ODP na nich polegają).

## P3 — warstwa API/dostępu (świadomie NIE ruszać teraz)

### 11. Trzy mechanizmy limitowania + ręczny sliding window
AspNetCore FixedWindow (HTTP), ręczny `RateGuard` (Blazor; powiela `SlidingWindowRateLimiter`
z frameworka, stałe na sztywno), `CostGuard` (dzienny cap; miesza `lock` z `ConcurrentDictionary` —
mylący sygnał o modelu współbieżności).
**Propozycja:** zostawić do pilotażu (PLAN-STRATEGIA-PILOTAZ jawnie akceptuje te skróty);
przy okazji dowolnej zmiany w CostGuard: zwykły Dictionary pod lockiem.
**Ryzyka wymiany RateGuard:** partycjonowany limiter per user w obwodzie Blazora do skonfigurowania
poprawnie — zysk mały, możliwość regresji limitów realna. Niski priorytet — świadomie odroczyć.

### 12. Strona logowania jako inline HTML w Program.cs
**Propozycja:** plik statyczny / komponent Razor. **Ryzyka:** znikome. Kosmetyka.

### 13. Migracja `AddDemoConversations` nie zawiera danych demo (tworzy tabele)
**Propozycja:** NIE zmieniać nazwy (przepisywanie zastosowanych migracji gorsze niż myląca nazwa) —
tylko komentarz w pliku migracji. **Ryzyka zmiany nazwy:** rozjazd z tabelą __EFMigrationsHistory
na wszystkich bazach — dlatego nie ruszać.

## P4 — kosmetyka (robić „przy okazji", nie jako osobne zadania)

### 14. Helpery tekstowe skopiowane 8×
`Snippet` (GroundedPrompt + ChatService), `Normalize`/`Trim`/`Preview` (3× w Eval),
`Trunc` (ConversationStore + QualityReportRunner), `NormalizeForDedup` (retriever + ChunkProbe).
**Propozycja:** wspólny `TextUtils` w Domain. **Ryzyka:** znikome; wartość też mała.

### 15. Defaulty progów/parametrów rozproszone
`0.55` literalnie w 3 runnerach Eval mimo istnienia `AbstentionPolicy.DefaultThreshold`; podobnie
TopK=8/MinChunkTokens=20 (RetrievalQuery + RetrievalOptions + Eval). **Propozycja:** wszędzie
stałe domenowe. **Ryzyka:** zerowe.

### 16. Dwa parsery cytowań + słownictwo aliasów w 2 miejscach
`CitationParser` (pytania, hint per tekst) vs `JudgmentCitationParser` (orzeczenia, przyleganie) —
rozdział KONTRAKTÓW jest świadomy i słuszny; duplikacją jest tylko słownictwo (lista skrótów
+ mapowanie fraz „kodeks…" — ActAliases vs MapPhrase). **Propozycja:** współdzielić samą listę
aliasów/mapowanie fraz. **Ryzyka:** zachowanie QU — testy obu parserów istnieją; małe.

### 17. Sondy diagnostyczne duplikują SQL/fuzję retrievera — ZAAKCEPTOWANE
ActLaneProbe/ChunkProbe kopiują dense-SQL i wzory RRF świadomie (skomentowane: „rozjazd sondy
z retrieverem = zmiana bez aktualizacji sondy"). **Propozycja:** co najwyżej wyeksportować stałe
(RrfK, HnswEfSearch, CandidatesPerPath) z retrievera jako public — sondy przestają hardkodować
liczby. **Ryzyka:** zerowe.

---

## Rekomendowana ścieżka
1. Teraz: nic — trwa faza JAK (pomiary). Refactor w środku kalibracji zaszumiłby wyniki.
2. Po zamknięciu JAK: **#2 → #1** (pakiet groundingu, potem pipeline czatu) — z golden setem
   i `--refusals` jako bramką przed/po; #3 (test parytetu filtrów) równolegle, bo to sam test.
3. Ingestia (#4-#10): przy następnym realnym zadaniu ingestowym (np. delta-sync na produkcji),
   z twardym wymogiem równoważności bajtowej treści chunków (#5/#8).
4. Nigdy/na razie nie: zmiana nazwy migracji (#13), wymiana RateGuard (#11) przed pilotażem.
