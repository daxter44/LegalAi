# Plan: jakość „Analizy dokumentów" (AJ) — zadania z przeglądu 2026-09-02

Źródło: [PRZEGLAD-ANALIZA-DOKUMENTOW-KIERUNKI-2026-09-02.md](PRZEGLAD-ANALIZA-DOKUMENTOW-KIERUNKI-2026-09-02.md)
(kierunki K1–K9). Ten dokument rozpisuje je na zadania w konwencji planów DOC/SPK/AN: zakres,
pliki, testy, kryterium odbioru, zależności. Bez wycen czasowych. Status: **plan, brak „go"**.

Zasady wspólne:
- Każda zmiana promptu/pipeline'u przechodzi przez eval z AJ-1 PRZED i PO. Bez liczby nie ma merge.
- Prompt czatu bez analizy (`GroundedPrompt.SystemPrompt`) zostaje bajt w bajt — analiza dokłada
  własne bloki, nie modyfikuje wspólnych.
- Treść dokumentu nadal nigdy nie trafia do bazy (decyzja DOC #1). Każde zadanie, które tworzy
  nową pochodną treści dokumentu (profil, zagadnienia), musi jawnie rozstrzygnąć, czy ją persystuje.
- Commit per zadanie, testy zielone (`dotnet test`, Postgres lokalny na 5432).

## Decyzje (podjęte 2026-09-02, zgodnie z rekomendacjami)

| # | decyzja | ROZSTRZYGNIĘCIE | konsekwencja w zadaniach |
|---|---|---|---|
| D1 | Persystencja profilu dokumentu (AJ-3) | **NIE** — profil żyje tylko w `AnalysisSession`, jak treść § (spójne z DOC #1) | Dopytania z archiwum działają bez profilu; brak migracji dla profilu. |
| D2 | Streszczenie odpowiada wprost na pytanie użytkownika | **TAK** — meta-wniosek wyłącznie z nagłówka mechanicznego i werdyktów, twardy zakaz nowych przepisów/sygnatur/ocen | Zamyka otwarte pytanie 4 z raportu 07-23. AJ-6. |
| D3 | Zestaw werdyktów (AJ-5) | **OK / RYZYKO WYSOKIE / RYZYKO NISKIE / BEZ TREŚCI PRAWNEJ / POZA ZAKRESEM / BRAK PODSTAWY** | Brzmienie do ewentualnej korekty po pierwszym pokazaniu prawnikowi-testerowi, bez blokowania AJ-5. |
| D4 | Skład golden setu (AJ-0) | Umowy i regulamin z wbudowanymi wadami układa programista; decyzja administracyjna wchodzi z flagą `NeedsLawyer` i nie jest scorowana merytorycznie do przeglądu prawnika | AJ-14 czeka na przegląd prawnika i na dane o użyciu. |
| D5 | Kolejność faz 2 i 3 | **DOCX (AJ-11) i splitter (AJ-13) między AJ-2 a AJ-3**; reszta fazy 3 (AJ-12) po AJ-6 | Testerzy dostarczający dokumenty do golden setu od razu wgrywają Word. |

Status planu (2026-09-03): „go" 2026-09-02. **Zrobione i zacommitowane:** AJ-0, AJ-1a/1b, AJ-2,
AJ-3, AJ-4, **AJ-4b** (dodane: zapytanie retrievalu rozdzielone od promptu — patrz niżej), AJ-5,
AJ-6, AJ-11, AJ-12, AJ-13. **Czeka na dostęp do stacku (.11) i klucz LLM:** pomiar AJ-2 z generacją,
biegi AJ-4/4b (retrieval z samą treścią fragmentu vs treść + kotwica), AJ-7. **Nie zaczęte:** AJ-8…10, AJ-14.

### AJ-4b (dodane 2026-09-03 po pomiarze): zapytanie retrievalu ≠ prompt fazy map
- Pomiar: kotwica-wyrocznia dodana do promptu map nie zmieniła trafienia normy (3/17 → 3/17).
  Przyczyna: zapytaniem retrievalu był CAŁY prompt (intencja + kontekst + fragment + instrukcja
  „WERDYKT: OK / RYZYKO…"), ucinany przez embedder do 512 tokenów; kotwica tonęła w instrukcji.
- Zrobione: `IChatService.AskAsync(..., retrievalQuery)`, `AnalysisPrompts.RetrievalQuery(unit,
  profile)` = kotwica + treść fragmentu do 1800 znaków, bez intencji i instrukcji; runner i eval
  używają tego samego zapytania. Historia retrievalu przy podanym zapytaniu pusta.
- Do zmierzenia (dwa biegi `--no-generate`): sama treść (`--no-profile`) vs treść + kotwica
  (`--oracle-profile`). Wynik rozstrzyga, czy AJ-8 (zagadnienie jako zapytanie) jest konieczne.

## Faza 0 — pomiar (warunek zabicia dla reszty)

### AJ-0: Golden set analizy (dane)
- Nowy plik `src/PrawoRAG.Eval/analysis-set.json`. Model: `AnalysisGoldenDoc { Id, Kind, Prompt,
  Pages: string[], Units: [{ Heading, ExpectedVerdict, ExpectedEli?, ExpectedArticle?, PlantedRisk?,
  NeedsLawyer }] }`. `Pages` = tekst dokumentu (nie PDF — eval karmi `LegalUnitSplitter` bezpośrednio,
  PdfPig nie jest badany).
- Dokumenty v1 (4–6): umowa najmu lokalu mieszkalnego z wbudowanymi wadami (przenieść z
  `TEST-ZALACZNIK-UMOWA-NAJMU.md`), umowa z konsumentem z klauzulami abuzywnymi (kaucja, kara umowna,
  jurysdykcja), regulamin sklepu internetowego (odstąpienie, reklamacje), umowa o dzieło B2B (kontrola:
  swoboda umów, mało RYZYKO), decyzja administracyjna z powołanym orzecznictwem (`NeedsLawyer`).
- Każdy § ma oczekiwany werdykt; § z wbudowaną wadą ma `ExpectedEli/ExpectedArticle` normy, która
  powinna znaleźć się w źródłach (jak `expectedEli` w `golden-set.json`).
- Kryterium odbioru: plik waliduje się schematem, każdy dokument przechodzi przez `LegalUnitSplitter`
  i liczba jednostek zgadza się z liczbą wpisów `Units` (test jednostkowy — chroni klucz przed
  rozjazdem ze splitterem).
- Zależności: D4.

### AJ-1: Runner ewaluacji `--analysis`
- **Podzadanie AJ-1a (refaktor bez zmiany zachowania):** `LegalUnitSplitter`, `DocChunker`,
  `AnalysisPrompts`, `DocUnit`, `UnitVerdict` przenieść z `PrawoRAG.Api/Services` do
  `PrawoRAG.Llm` (lub `PrawoRAG.Domain/Documents`) — są czyste, zależą tylko od `GroundedPrompt`.
  Powód: `PrawoRAG.Eval` nie referencuje `Api` (celowo, jak `RefusalEvalRunner`, który odtwarza
  pipeline z `IRetriever` + `GroundedPrompt` + `ILlmProvider`). Testy istniejące przechodzą bez zmian
  (tylko namespace).
- **AJ-1b:** `AnalysisEvalRunner` w `PrawoRAG.Eval`: per dokument → splitter → per jednostka
  `MapQuestion` → retrieval (`IRetriever`) → `GroundedPrompt.Build` → LLM → `ParseVerdict`.
  Odtwarza fazę map runnera bez `ChatService` (drugą rundę retrievalu i `AnswerGate` odnotować jako
  różnicę względem produkcji — jak zastrzeżenie w `RefusalEvalRunner`).
- Metryki w raporcie (tabela + JSON snapshot do `docs/`):
  - recall wbudowanych ryzyk (§ z `PlantedRisk` → werdykt RYZYKO),
  - fałszywe RYZYKO (§ z oczekiwanym OK → RYZYKO),
  - BRAK ŹRÓDEŁ na § z treścią prawną,
  - trafienie normy (`ExpectedEli/Article` wśród źródeł jednostki) — metryka retrievalu niezależna od LLM,
  - czas per jednostka i per dokument (`LatencyLog` już mierzy etapy).
- Tryb `Eval:AnalysisGenerate=false` = tylko retrieval + trafienie normy (tanie, bez LLM) — jak
  `RefusalsGenerate`.
- Kryterium odbioru: jeden przebieg na pełnym stacku, wynik zapisany jako
  `docs/EWALUACJA-ANALIZA-BASELINE-<data>.md`. To jest **baseline** dla wszystkich kolejnych zadań.
- Zależności: AJ-0.

### AJ-2: Pomiar czasu i `finish_reason`
- Jeden przebieg `/analiza` na dokumencie ~15 jednostek z `PRAWORAG_LOG_TIMING=1`; rozkład:
  retrieval / pierwszy token / generacja / częstość drugiej rundy i regeneracji `AnswerGate`.
- Kod (mały): `OpenAiCompatibleLlmProvider` czyta `finish_reason` z ostatniego zdarzenia SSE i loguje
  ostrzeżenie przy `length`; `AnalysisRunner` zapisuje `FinishReason` w `UnitAnalysis` (kolumna
  nullable w `analysis_units`, migracja). Zamyka hipotezę „? = budżet myślenia" z raportu niezawodności.
- Kryterium odbioru: `docs/POMIAR-ANALIZA-CZAS-<data>.md` z rozkładem i wskazaniem dźwigni
  (cache prefiksu / równoległość na backendzie z batchingiem / budżet rozumowania / pominięcie
  jednostek bez treści prawnej).
- Zależności: brak (równolegle z AJ-0/1).

## Faza 1 — profil dokumentu i bogatszy werdykt (K2 + K4)

### AJ-3: Profil dokumentu (`DocumentProfile`)
- Nowy krok w `AnalysisRunner.RunAsync` przed fazą map (po embeddingach jednostek): jedno wywołanie
  LLM (`ILlmProvider` główny, temperatura 0) na próbce: „wstęp" + pierwsze N jednostek do budżetu
  ~3000 znaków. Format liniowy, parsowany twardo (wzorzec KAZ): `TYP:`, `STRONY:` (rola + status:
  konsument / przedsiębiorca / organ), `PRZEDMIOT:`, `DEFINICJE:`, `POWOŁANE AKTY:`,
  `POWOŁANE ORZECZENIA:`. Brak linii = pole puste; 0 linii = brak profilu, analiza działa jak dziś.
- Prompt: WYŁĄCZNIE fakty z tekstu, zakaz ocen prawnych i cytowań [n]. Strażnik w kodzie:
  `DocumentProfile.IsClean` odrzuca profil zawierający słowa oceny („narusza", „niezgodn",
  „nieważn", „abuzywn") lub markery `[n]` — wtedy profil = null (fail-safe w stronę dzisiejszego
  zachowania).
- Persystencja wg D1 (rekomendacja: nie; profil w `AnalysisSession` jak treść §).
- Pliki: `AnalysisPrompts.ProfilePrompt/ParseProfile`, `AnalysisSession.Profile`, `AnalysisRunner`.
- Testy: parser (pełny profil, częściowy, pusty), strażnik czystości (profil z oceną → null),
  runner z fałszywym LLM wywołuje profil raz per dokument, nie per jednostka.
- Kryterium odbioru: testy zielone; `PRAWORAG_DUMP_PROMPT=1` pokazuje blok PROFIL w promptach map.

### AJ-4: Profil w prompcie map i w zapytaniu retrievalu
- `AnalysisPrompts.MapQuestion(userPrompt, unit, profile?)`: blok „KONTEKST DOKUMENTU (fakty z
  całości): …" NAD fragmentem. Bez profilu prompt identyczny z dzisiejszym (Assert.Equal — zero
  regresji).
- Kotwica dziedzinowa dla retrievalu: do treści zapytania dokładana jedna linia z `TYP` + `STRONY`
  (np. „umowa najmu lokalu mieszkalnego; najemca konsument"). Uwaga: `ChatService` używa treści
  pytania jako zapytania retrievalu — QU/reformulator Aux dostanie ten sam tekst, sprawdzić w
  dumpie, że kotwica przeżywa reformulację.
- Testy: numeracja i kolejność bloków; brak profilu = dzisiejszy prompt.
- Kryterium odbioru: eval AJ-1 PO vs baseline — oczekiwany wzrost „trafienia normy" i recallu
  na klauzulach ogólnych. **Warunek zabicia:** jeśli trafienie normy nie rośnie, kotwica nie
  działa — nie wchodzić w AJ-8 bez zrozumienia dlaczego.
- Zależności: AJ-3, AJ-1.

### AJ-5: Zestaw werdyktów i akcjonowalne RYZYKO
- `UnitVerdict`: dodać `RiskHigh`, `RiskLow`, `NoLegalContent`, `OutOfScope`; `Risk` zostaje jako
  legacy do odczytu starych rekordów (`AnalysisStore.ParseVerdict` już degraduje nieznane do
  `Unknown`, ale stare „Risk" ma się nadal wyświetlać jako ryzyko).
- Prompt map: pierwsza linia z nowego zestawu (brzmienie wg D3); dla RYZYKO dwie obowiązkowe linie
  po werdykcie: `NARUSZA: …` (norma z [n]) i `DO ROZWAŻENIA: …` (co zmienić w klauzuli).
  `ParseVerdict` wyciąga je do `UnitAnalysis.Violates` / `Suggestion` (nullable, persystowane).
- Reguła w prompcie dla `POZA ZAKRESEM`: fragment opiera się na akcie prawa miejscowego lub
  dokumencie zewnętrznym (plan miejscowy, regulamin, załącznik) — nazwać ten dokument w
  uzasadnieniu.
- UI (`Analiza.razor`): badge per werdykt, sekcje BEZ TREŚCI PRAWNEJ zwinięte domyślnie, POZA
  ZAKRESEM z jednym zdaniem wyjaśnienia zamiast „brak źródeł"; licznik „nieudanych" dla retry
  obejmuje `Error` i `Unknown`.
- Migracja: brak (werdykt to string), ale `Violates`/`Suggestion` = dwie kolumny nullable.
- Testy: parser dla każdego werdyktu, linie NARUSZA/DO ROZWAŻENIA obecne/nieobecne, stare
  rekordy „Risk" mapują się na ryzyko, `Label` dla wszystkich wartości.
- Kryterium odbioru: eval AJ-1 rozszerzony o rozkład nowych werdyktów; udział generycznego
  „BRAK ŹRÓDEŁ" na § bez treści prawnej spada do ~0 (to reklasyfikacja, nie poprawa modelu —
  nazwać to wprost w raporcie).
- Zależności: D3, AJ-1.

### AJ-6: Nagłówek mechaniczny i streszczenie odpowiadające na pytanie
- `AnalysisReport.Headline(results)` (czysta funkcja): „N z M § z ryzykiem (wysokie: § 5, § 7;
  niskie: § 12); K § poza zakresem korpusu; L § bez treści prawnej". Liczone w C#, bez LLM,
  wyświetlane nad streszczeniem i wchodzące do kotwicy dopytań (`AnalysisFollowUp.ComposeAnchorTurn`).
- `SummarySystemPrompt`: wg D2 — dostaje nagłówek i ma prawo sformułować meta-wniosek WYŁĄCZNIE
  z werdyktów („Biorąc pod uwagę 3 fragmenty z ryzykiem wysokim, odwołanie ma podstawy w
  zakresie…"), nadal zakaz nowych przepisów/sygnatur/ocen spoza wyników.
- Testy: nagłówek dla mieszanki werdyktów, dla samych OK, dla pustego raportu; kotwica dopytań
  zawiera nagłówek z przodu (budżet 1500 znaków).
- Kryterium odbioru: przegląd ręczny 3 streszczeń na golden secie — czy odpowiadają na pytanie
  bez dokładania twierdzeń (checklista w dokumencie sesji).
- Zależności: AJ-5, D2.

### AJ-7: Eval fazy 1 i decyzja
- Pełny przebieg AJ-1 po AJ-3…6; porównanie z baseline w `docs/EWALUACJA-ANALIZA-FAZA1-<data>.md`.
- **Warunek zabicia całej ścieżki jakości:** jeśli recall wbudowanych ryzyk nie rośnie przy
  rosnącym trafieniu normy, wąskim gardłem jest model, nie pipeline (przypadek OKI po
  rozszerzeniu sąsiedztwa). Wtedy AJ-8…10 czekają na zmianę backendu LLM, a plan przechodzi do
  fazy 3.

## Faza 2 — zagadnienie prawne przed retrievalem (K3)

### AJ-8: `IssueSpotter` na modelu Aux
- `PrawoRAG.Llm/AuxIssueSpotter.cs` na wzór `AuxDocumentGate`: wejście = profil (jeśli jest) +
  treść jednostki; wyjście = 0–2 linie `ZAGADNIENIE: <pytanie prawne>`; format liniowy, parser
  twardy, fail-open = 1 zagadnienie „oceń zgodność fragmentu z prawem" (czyli dzisiejsze
  zachowanie). Asymetria promptu w stronę „jest zagadnienie" — fałszywe pominięcie klauzuli jest
  droższe niż zbędny retrieval.
- Testy: parser (0/1/2/nadmiar linii, śmieci), fail-open na wyjątek/timeout, asercja że dla
  próbki „komparycja" i „§ o karze umownej" fałszywy LLM daje odpowiednio 0 i ≥1 (test kontraktu
  promptu na fake, nie na modelu).
- Kryterium odbioru: testy zielone; ręczna próba na 20 jednostkach z golden setu z tabelą
  „pominięte / trafione zagadnienie" w dokumencie sesji.
- Zależności: AJ-7 (pozytywny wynik), AJ-3.

### AJ-9: Integracja w runnerze
- `AnalyzeUnitAsync`: przed retrievalem `IssueSpotter`; 0 zagadnień → `UnitAnalysis(NoLegalContent)`
  bez retrievalu i bez głównego LLM (nadal `MarkUnitLive`, upsert, `RecordAsync` z 0 znaków;
  bramka pojemności `CostGuard` nie jest pobierana — nie było wywołania głównego LLM).
- 1–2 zagadnienia → pytanie map = prompt użytkownika + zagadnienia + fragment. Punkt projektowy:
  `ChatService.AskAsync` używa treści pytania jako zapytania retrievalu; zagadnienia na początku
  pytania sterują retrievalem, fragment daje kontekst BM25. Jeśli dump pokaże, że fragment
  dominuje zapytanie, potrzebny będzie overload `AskAsync(question, retrievalQuery, …)` — zaznaczyć
  jako ryzyko, nie robić na zapas.
- UI: stan na żywo „rozpoznaję zagadnienie…" (rozszerzenie `UnitLiveState`).
- Testy: runner z fałszywym spotterem — 0 zagadnień nie woła `IChatService`; 2 zagadnienia
  trafiają do pytania; fail-open spottera = dzisiejsze pytanie.
- Kryterium odbioru: czas per dokument na golden secie spada proporcjonalnie do udziału jednostek
  bez treści prawnej (pomiar jak w AJ-2), recall nie spada.
- Zależności: AJ-8.

### AJ-10: Eval fazy 2
- Przebieg AJ-1 z metryką dodatkową: **fałszywe pominięcie** (§ z `PlantedRisk` → BEZ TREŚCI
  PRAWNEJ). Warunek zabicia: fałszywe pominięcie > 0 na golden secie = spotter wraca do prompt
  tuningu albo wyłączenia (flaga `Analysis:IssueSpotterEnabled`, domyślnie off do czasu wyniku).
- Zależności: AJ-9.

## Faza 3 — bariery użycia (K5, K6, K9)

### AJ-11: Wejście DOCX
- `DocxAttachmentExtractor` (`DocumentFormat.OpenXml`, tylko odczyt): akapity z zachowanym
  łamaniem → `Pages` (jedna „strona" per sekcja lub porcja ~3000 znaków); te same limity (10 MB,
  odpowiednik 100 stron) i typ wyniku co `PdfAttachmentExtractor` — wspólny interfejs
  `IAttachmentExtractor` wybierany po rozszerzeniu. Bramka skanów nie dotyczy DOCX.
- UI: `accept=".pdf,.docx"`, komunikaty; `/o-systemie` aktualizacja listy formatów.
- Testy: fixture DOCX z § → jednostki z nagłówkami; plik uszkodzony → czytelny błąd; DOCX z samymi
  obrazami → „brak tekstu".
- Kryterium odbioru: golden set umowy najmu wgrany jako DOCX daje te same jednostki co PDF.
- Zależności: brak (niezależne od faz 1–2).

### AJ-12: Eksport raportu
- Krok 1 (tani): „Kopiuj raport" jako Markdown (nagłówek, werdykty, uzasadnienia, źródła jako
  linki) — czysta funkcja `AnalysisReport.ToMarkdown(snapshot)`; działa też w trybie z archiwum.
- Krok 2: arkusz `@media print` dla `/analiza/{id}` (drukuj do PDF z przeglądarki) — zero
  renderingu serwerowego.
- Krok 3 (osobna decyzja): DOCX raportu po stronie serwera, dopiero gdy krok 1–2 okażą się
  niewystarczające.
- Testy: `ToMarkdown` dla raportu pełnego i zdegradowanego (bez treści §).
- Zależności: AJ-6 (nagłówek w eksporcie) — może iść wcześniej bez nagłówka.

### AJ-13: Splitter — cięcie na granicy ustępu
- `LegalUnitSplitter.SplitOversize`: przed twardym cięciem po spacji próbować granic
  `\bust\.\s*\d+` / `\bpkt\s*\d+` / kropka+nowa linia; części „(cz. n)" zachowują nagłówek §.
- Testy: § 3600 znaków z trzema ustępami → cięcie między ustępami; brak ustępów → dotychczasowe
  zachowanie; determinizm.
- Zależności: brak.

## Faza 4 — warunkowo (K8)

### AJ-14: Weryfikacja powołanych orzeczeń (most orzeczenie→orzeczenie)
- Wchodzi tylko, jeśli AJ-0/D4 pokażą, że decyzje i pisma to istotna część użycia. Zakres i
  warianty (sygnatura → `CaseNumber`; sąd + data → `JudgmentDate` + `court`) opisane w
  [RAPORT-JAKOSC-ANALIZY-DOKUMENTOW-2026-07-23.md](RAPORT-JAKOSC-ANALIZY-DOKUMENTOW-2026-07-23.md)
  i [PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md](PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md) (Wariant B).
  Wymaga osobnego planu — ścieżka retrievalu po metadanych dotyka `HybridRetriever`, nie tylko
  analizy.

## Kolejność i zależności

Kolejność wykonania (po D5):

```
1. AJ-0 golden set ─► 2. AJ-1 runner + baseline      (AJ-2 pomiar czasu równolegle)
3. AJ-11 DOCX, AJ-13 splitter                        (małe, niezależne — tarcie testerów)
4. AJ-3 profil ─► AJ-4 profil w prompcie ─► AJ-5 werdykty ─► AJ-6 nagłówek+streszczenie
5. AJ-7 eval fazy 1 ─► [go/kill]
6. AJ-12 eksport raportu (kopiuj Markdown + druk)
7. AJ-8 ─► AJ-9 ─► AJ-10 (tylko przy „go" z AJ-7)
8. AJ-14 — osobny plan, po przeglądzie prawnika (D4) i danych o użyciu
```

## Poza zakresem tego planu (jawnie)
- OCR skanów (decyzja koszt/model, osobno).
- Wiele dokumentów w jednej analizie; porównanie wersji umowy.
- Anonimizacja przed LLM (kierunek #2 planu KAZ).
- Zmiana backendu LLM / równoległości — to decyzja infrastrukturalna, plan tylko mierzy jej potrzebę (AJ-2, AJ-7).
