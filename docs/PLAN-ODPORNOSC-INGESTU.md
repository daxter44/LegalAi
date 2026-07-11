# Plan: odporność masowego processingu (ODP) — wznowienie, bezpiecznik, diagnostyka

## Kontekst i problem

Przed masowym embeddingiem pełnego korpusu (~551 tys. dokumentów, ~4,9 mln chunków, wiele godzin
na GPU) proces `Ingestion:Mode=process` ma trzy słabości, które przy awarii w środku nocy
zamieniają się w stracone godziny:

1. **Drogie wznowienie.** Idempotencja działa (hash + status), ale skip już zrobionego dokumentu
   kosztuje: pełny odczyt i deserializację pliku JSON (~87 KB), SHA-256 **oraz** zapytanie do
   Postgresa z `Include(d => d.Chunks)`, które ładuje wszystkie chunki Z WEKTORAMI 4 KB
   (`IngestionPipeline.cs:36-38`) — tylko po to, by sprawdzić hash i `EmbeddedWith`.
   Wznowienie od 50% = ~275 tys. takich skipów = **1–3 h zanim ruszy realna praca**.
2. **Awaria infrastruktury udaje awarię dokumentów.** `ProcessAsync` łapie każdy wyjątek per
   dokument → `MarkFailedAsync` → jedzie dalej. Gdy padnie TEI/DB/sieć, proces NIE zatrzyma się —
   oznaczy `Failed` każdy kolejny dokument do końca magazynu. Rano: `failed=275000`, zmarnowany
   przebieg, tabela pełna śmieciowych statusów.
3. **Uboga diagnostyka porażki.** `FailureReason` = samo `ex.Message` ucięte do 1000 znaków, bez
   etapu (normalizacja? chunking? embedding? zapis?), bez stack trace, bez pozycji w przebiegu.
   Log leci na konsolę — po nocnym runie scrollback nie istnieje. Odpowiedź na „co się stało
   z dokumentem nr 59584" wymaga ponownego uruchomienia i debugowania.
4. **(bonus znaleziony przy analizie)** `ReEmbedAsync` jest wywoływany POZA try/catch
   (`IngestionPipeline.cs:45`) — wyjątek TEI na ścieżce warunkowego re-embeddingu wywala cały run
   bez `MarkFailed` i bez kontekstu.

Cel: pad w nocy → rano naprawa przyczyny → restart → **minuty** „przewijania" → proces liczy
dalej od miejsca awarii, a każda porażka per dokument jest w pełni opisana bez ponownego runu.

---

## ODP-0: interfejs `IIngestionPipeline` (przygotowanie pod testy)

**Problem:** `RawProcessRunner` resolwuje KONKRETNĄ klasę `IngestionPipeline` z scope'a — nie da
się w testach jednostkowych podstawić fake'a sterującego sekwencją wyników (`Failed×10` itd.).

**Zmiany:**
- `src/PrawoRAG.Ingestion/IngestionPipeline.cs` — wydzielić interfejs:
  ```csharp
  public interface IIngestionPipeline
  {
      Task<IngestOutcome> ProcessAsync(RawDocument raw, CancellationToken ct);
  }
  ```
  (osobny plik `IIngestionPipeline.cs`), `IngestionPipeline : IIngestionPipeline`.
- `IngestionServiceCollectionExtensions.cs:69` — dodać mapowanie na TEN SAM scoped obiekt:
  ```csharp
  services.AddScoped<IngestionPipeline>();
  services.AddScoped<IIngestionPipeline>(sp => sp.GetRequiredService<IngestionPipeline>());
  ```
- `RawProcessRunner.cs:28` — `GetRequiredService<IIngestionPipeline>()`.
- `IngestionRunner.cs` (ścieżka fetch-process/stream) — analogicznie, jeśli resolwuje pipeline.

**Bez zmian zachowania.** Czysty refaktor; `dotnet build` + istniejące testy zielone.

---

## ODP-1: fast-skip set — tanie wznowienie

**Zasada:** semantyka skipu IDENTYCZNA z pipeline'em, tylko sprawdzona hurtowo jednym zapytaniem
zamiast 275 tys. roundtripów. Pipeline zostaje nietknięty (zweryfikowana logika idempotencji).

**Nowy plik `src/PrawoRAG.Ingestion/ProcessSkipSet.cs`:**
- Czysta klasa trzymająca `HashSet<string>` z kluczem `$"{ExternalId}\n{ContentHash}"`;
  metoda `bool Contains(string externalId, string contentHash)`. Testowalna bez DB.
- Scoped loader (`ProcessSkipSetLoader`, DbContext + `IEmbeddingProvider` w konstruktorze):
  ```csharp
  var set = await db.Documents
      .Where(d => d.Source == source
               && d.Status == DocumentStatus.Indexed
               && !d.Chunks.Any(c => c.EmbeddedWith != embedder.ModelId))
      .Select(d => new { d.ExternalId, d.ContentHash })
      .ToListAsync(ct);
  ```
  Uwaga na równoważność z pipeline'em: dokument Indexed z 0 chunków przechodzi warunek
  `!Any(...)` — tak samo jak dziś `stale.Count == 0 → Skipped`. Dokumenty `Failed`, ze zmienioną
  treścią lub ze starym modelem embeddingu NIE trafiają do zbioru → spadają do pipeline'u jak dziś.
- Rejestracja: `AddScoped<ProcessSkipSetLoader>()` w `IngestionServiceCollectionExtensions`.

**`RawProcessRunner.RunAsync`:**
- Na starcie: osobny scope → `ProcessSkipSetLoader.LoadAsync(source, ct)`;
  log `Information`: „Fast-skip: {N} dokumentów już zaindeksowanych (model {ModelId})".
- W pętli PRZED tworzeniem scope'a:
  ```csharp
  var hash = Hashing.Sha256Hex(raw.RawContent);   // ta sama funkcja co w pipeline
  if (skipSet.Contains(raw.ExternalId, hash)) { skipped++; continue; }
  ```
  Hash liczony w runnerze jest przekazywany dalej? NIE — pipeline liczy własny (duplikacja
  ~0,1 ms/dok jest pomijalna, a brak zmiany sygnatury `ProcessAsync` = zero ryzyka).
- Kill-switch: `Ingestion:FastSkip` (default `true`) — czytane w `Program.cs` i przekazywane do
  `RunAsync` (parametr `bool fastSkip = true`) — awaryjny powrót do starej ścieżki bez rebuilda.

**Świadome decyzje (zapisać w komentarzach):**
- ~100 MB RAM na 551 tys. wpisów — akceptowalne (M4/serwer), jednorazowo per run.
- `maxItems` liczy TAKŻE fast-skipy (parytet z obecnym zachowaniem `processed++`).
- Odczyt pliku JSON nadal się dzieje (EnumerateAsync musi go przeczytać) — koszt wznowienia
  spada do ~1–3 ms/dok ≈ **5–15 min na 275 tys.**, bez ryzykownych sztuczek z nazwami plików
  (sanityzacja `/`→`_` jest stratna, nie odwracamy jej).

---

## ODP-2: bezpiecznik — seria porażek przerywa run

**`RawProcessRunner.RunAsync`:**
- Licznik `consecutiveFailures`: `Failed` → `++`; KAŻDY inny wynik (Inserted/Updated/Skipped/
  ReEmbedded oraz fast-skip) → zerowanie.
- Po przekroczeniu progu (default **10**, konfig `Ingestion:FailStreakLimit`):
  - log `Critical`: próg, ostatni dokument (`Source/ExternalId`, pozycja `#processed`), ostatni
    błąd, dotychczasowe podsumowanie liczników + ścieżka raportu JSONL (ODP-3);
  - `throw new ProcessAbortedException(...)` (nowy typ w `RawProcessRunner.cs` lub obok) —
    proces kończy się niezerowym kodem wyjścia (Program.cs nie łapie → OK dla crona/skryptu).
- Komunikat MUSI mówić wprost: „{N} porażek z rzędu — to wygląda na awarię infrastruktury
  (TEI/DB/sieć), nie na złe dokumenty. Napraw przyczynę i uruchom ponownie — fast-skip przewinie
  gotowe, a dokumenty Failed z tej serii zostaną przetworzone od nowa."
- Semantyka po restarcie: dokumenty z serii mają `Failed` w DB → nie ma ich w skip-set →
  reprocessing naprawia je automatycznie. Zero ręcznego sprzątania.
- `OperationCanceledException` (Ctrl+C) nie liczy się jako porażka — propaguje jak dziś.

**Konfiguracja:** czytana w `Program.cs` (`cfg["Ingestion:FailStreakLimit"]`, default 10),
przekazana parametrem do `RunAsync`. Wartość `0` = bezpiecznik wyłączony (dokumentować).

---

## ODP-3: pełna diagnostyka porażki per dokument

### 3a. Etap porażki (stage) w pipeline
- `ProcessFreshAsync`: zmienna `stage` aktualizowana przed każdym krokiem:
  `"normalize"` → `"chunk"` → `"embed"` → `"db-write"`. Wyjątek złapany w `ProcessAsync`
  dostaje prefiks: `FailureReason = $"[{stage}] {ex.GetBaseException().Message}"`
  (GetBaseException, bo `HttpRequestException` z TEI opakowuje właściwą przyczynę).
  Technicznie: `ProcessFreshAsync` wyrzuca wyjątek opakowany we własny
  `IngestionStageException(stage, inner)` LUB zwraca stage przez pole/out — wybrać wariant
  z wyjątkiem (nie zmienia sygnatur publicznych).
- **Fix bonusowy:** wywołanie `ReEmbedAsync` (linia 45) WCIĄGNĄĆ pod try/catch z `stage="re-embed"`
  — dziś wyjątek TEI na tej ścieżce wywala cały run bez `MarkFailed`.

### 3b. Kontekst pozycji w runnerze
- Log porażki w `RawProcessRunner` (po `case Failed`): 
  `„Porażka #{processed}: {Source}/{ExternalId}"` — natychmiast wiadomo, że to „dokument nr 59584"
  i który to plik (`{RootPath}/{source}/{sanitized-id}.json`).
- Log postępu co 50 rozszerzyć o ostatni `ExternalId` — po twardym padzie (OOM/kill) ostatnia
  linia lokalizuje pozycję z dokładnością ±50 (wznowienie i tak jest tanie po ODP-1).

### 3c. Raport porażek JSONL (pełny, poza DB)
- Nowy plik `src/PrawoRAG.Ingestion/FailureReport.cs`: append-only writer, jeden JSON na linię,
  `File.AppendAllText`/StreamWriter z flush per linia (crash-safe), katalog z konfiguracji
  `Ingestion:FailureLogDir` (default `logs/`), nazwa `process-failures-{source}-{yyyyMMdd-HHmmss}.jsonl`.
- Rekord: `{ seq, source, externalId, docType, title?, stage, error (PEŁNY ex.ToString(), ze stack
  i inner), rawPath, timestamp }`.
- Zapis wywołuje runner przy każdym `Failed` (dane o stage/error musi dostać z pipeline'u —
  patrz 3a: `IngestionStageException` niesie oba; alternatywnie `ProcessAsync` zwraca bogatszy
  wynik `record IngestResult(IngestOutcome, string? Stage, Exception? Error)` — WYBRAĆ wariant
  IngestResult, bo bezpiecznik (ODP-2) też potrzebuje „ostatniego błędu" do komunikatu).
  Uwaga: zmiana typu zwracanego `ProcessAsync` = dotknięcie `IngestionRunner` i testów pipeline'u.
- Na końcu runa, jeśli `failed > 0`: log ze ścieżką raportu + gotowe zapytanie:
  `SELECT "ExternalId", "FailureReason", "AttemptCount" FROM documents WHERE "Status" = <Failed>;`

### 3d. `FailureReason` w DB
- Prefiks `[stage]` + `GetBaseException().Message`; limit 1000 znaków zostaje (pełny błąd żyje
  w JSONL).

---

## ODP-4: testy i runbook

**Testy (`tests/PrawoRAG.Tests/Ingestion/RawProcessRunnerTests.cs`, fake `IIngestionPipeline`
+ fake `IRawDocumentStore` w pamięci):**
1. Fast-skip: dokument w skip-set → pipeline NIE jest wywoływany, `skipped++`.
2. Fast-skip: hash inny niż w zbiorze → pipeline JEST wywoływany (zmiana treści przechodzi).
3. Bezpiecznik: sekwencja `Failed×10` → `ProcessAbortedException`, run przerwany na 10. dokumencie.
4. Bezpiecznik: `Failed×9, Skipped, Failed×9` → run dochodzi do końca (licznik się zeruje).
5. Bezpiecznik: limit `0` → 20×Failed nie przerywa.
6. `ProcessSkipSet.Contains` — trafienie/pudło/casing/`\n` w kluczu.
7. Raport JSONL: po `Failed` plik zawiera linię z externalId + stage + pełnym błędem (parsowalną
   `JsonDocument.Parse`).
8. Pipeline (istniejące testy? jeśli brak — dopisać): wyjątek w embed → `FailureReason` zaczyna
   się od `[embed]`; wyjątek w ReEmbed → outcome `Failed`, NIE wyjątek z `ProcessAsync`.
9. Regresja: cały `dotnet test` zielony (poza znanymi integracyjnymi PG).

**Runbook (`docs/RUNBOOK-EMBEDDING-ZDALNY.md` — dopisać sekcję „Awaria i wznowienie"):**
- uruchamiać z zapisem logu do pliku (`... 2>&1 | tee logs/process-YYYYMMDD.log`);
- co znaczy komunikat bezpiecznika i co sprawdzić (kontener TEI, `docker logs`, sieć, DB);
- wznowienie = to samo polecenie; oczekiwane `Fast-skip: N...` na starcie;
- gdzie leży raport porażek i jak go czytać (`jq`), zapytanie SQL o `Failed`;
- konfiguracja: `Ingestion:FastSkip`, `Ingestion:FailStreakLimit`, `Ingestion:FailureLogDir`.

---

## Kolejność implementacji

ODP-0 → ODP-1 → ODP-2 → ODP-3 (3a przed 3c — wariant `IngestResult` decyduje o kształcie) → ODP-4.
Każdy krok kompilowalny i testowalny osobno; commit per ODP.

## Poza zakresem (jawnie)

- Checkpoint pozycji w enumeracji plików (kolejność `Directory.EnumerateFiles` niegwarantowana;
  fast-skip załatwia problem semantycznie poprawnie).
- Równoległość processingu (osobny temat: batching do TEI już jest; zrównoleglenie per dokument
  wymaga przemyślenia transakcji i backpressure — nie mieszać z odpornością).
- Retry automatyczny z backoffem wewnątrz runa (bezpiecznik + restart ręczny wystarczą na
  jednorazową kampanię; wrócić przy codziennym sync, jeśli zacznie doskwierać).
