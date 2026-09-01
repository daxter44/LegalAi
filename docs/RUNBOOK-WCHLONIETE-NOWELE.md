# Runbook: wchłonięte nowelizacje poza torami semantycznymi retrievalu (M4)

Data: 2026-09-01. Kontekst i pomiary: `ANALIZA-NADGODZINY-WCHLONIETE-NOWELE-POMIAR-2026-09-01.md`
(+ diagnoza `DIAGNOZA-NADGODZINY-PRZESTARZALA-TRESC-NOWELI-2026-09-01.md`). Decyzja właściciela:
wdrożenie filtra + reguła 6a promptu jako uzupełnienie.

## Co robi zmiana

1. Nowa kolumna `documents."AbsorbedAmendment"` (bool, default **false**) — „ustawa nowelizująca,
   której zmiany żyją już w tekście jednolitym". Wyliczana zbiorczo: tytuł nowelizacyjny
   (`o zmianie ustaw…` / `o zmianie niektórych ustaw…`) + ELI nieobecne na żadnej liście
   `unabsorbedAmendments`. Oczekiwana skala: **~2 294 akty / ~421 tys. chunków (31% toru aktów)**.
2. Tory SEMANTYCZNE retrievalu (gęsty, BM25, akronimowy) pomijają oflagowane akty. Tory dokładne
   (sygnatura, Dz.U./ELI, cytat artykułu), most cytowań i augmenter — **bez zmian** (jawne
   wskazanie noweli, w tym jej przepisów przejściowych, nadal działa; świeże nowele bez flagi).
3. Flaga utrzymuje się sama: przeliczenie biegnie na końcu relinku (`sync-eli`), w obie strony.
4. Prompt: reguła 6a — przy kilku wersjach tego samego przepisu w źródłach stawki/liczby tylko
   z wersji najnowszej.

**Kluczowa własność rolloutu:** sam deploy kodu NICZEGO nie zmienia w wynikach — kolumna startuje
z `false`, więc filtr nie łapie żadnego wiersza. Zachowanie zmienia dopiero backfill (krok 4).
Rollback = wyzerowanie kolumny (sekcja na końcu), bez cofania kodu.

## Prekondycje

1. Reprocess ustępów ZAKOŃCZONY i zweryfikowany (`RUNBOOK-REPROCESS-USTEPY.md`) — nie mieszać
   dwóch zmian w jednym pomiarze golden setu.
2. Żadna inna ingestia nie działa; TEI żywy (`curl -s http://192.168.100.11:8080/info | head -c 100`).

## Kroki

```bash
cd ~/PrawoRAG && git pull
export ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag"
export Embeddings__BaseUrl=http://192.168.100.11:8080
```

### 1. Golden set BASELINE (PRZED)

```bash
cd src/PrawoRAG.Eval
dotnet run -c Release 2>&1 | tee /tmp/golden-przed-nowele.log
```

### 2. Migracja bazy (dodaje kolumnę, default false — bez wpływu na wyniki)

```bash
cd ~/PrawoRAG
dotnet ef database update --project src/PrawoRAG.Storage --startup-project src/PrawoRAG.Api
```

### 3. Restart aplikacji (nowy kod retrievera + reguła 6a; filtr jeszcze „pusty")

Standardowy restart procesu PrawoRAG.Api. Szybki sanity: zachowanie czatu bez zmian.

### 4. Backfill flag (TO włącza filtr merytorycznie)

```bash
cd src/PrawoRAG.Ingestion
Ingestion__Mode=absorbed-flags dotnet run -c Release
# oczekiwane: ABSORBED-FLAGS DONE: changed=~2294 (idempotentne — drugi bieg: changed=0)
```

### 5. Weryfikacja danych (SQL, read-only)

```sql
-- oczekiwane rzędy wielkości: ~2294 aktów / ~421 tys. chunków
SELECT count(*) FROM documents WHERE "AbsorbedAmendment";
SELECT count(*) FROM chunks c JOIN documents d ON d."Id"=c."DocumentId" WHERE d."AbsorbedAmendment";

-- konkretni winowajcy z diagnozy MAJĄ flagę:
SELECT "ExternalId", "AbsorbedAmendment" FROM documents
WHERE "ExternalId" IN ('DU/1996/110','DU/2002/1146','DU/2003/2081') AND "Source"='ELI';
-- Kodeks pracy i świeże nowele KPC NIE mają flagi:
SELECT "ExternalId", "AbsorbedAmendment" FROM documents
WHERE "ExternalId" IN ('DU/1974/141','DU/2026/473') AND "Source"='ELI';
```

### 6. Golden set PO — bramka

```bash
cd ../PrawoRAG.Eval
dotnet run -c Release 2>&1 | tee /tmp/golden-po-nowele.log
```

**Kill-condition (wtedy rollback, sekcja niżej):**
- spadek `FreshnessRecall` (strażnik AKT — świeże nowele nie mogą zniknąć z odpowiedzi),
- spadek trafień ogółem poza szum pojedynczego pytania.
Spodziewany kierunek: bez regresji; możliwa poprawa na pytaniach o często nowelizowane przepisy.

### 7. Pytanie-nośnik na żywym czacie

„Jakie wynagrodzenie przysługuje mi za nadgodziny?" — odpowiedź ma cytować **art. 151¹ §1 KP
(100%/50% zależnie od pory/dnia)**, NIE stawkę „50% za dwie pierwsze godziny" z 1996 r.
Uwaga: pomiar wykazał, że sam filtr wnosi do puli art. 151¹ **§2**, a §1 dociąga sąsiedztwo —
jeśli odpowiedź nadal nie widzi §1, zanotować i wrócić z ChunkProbe (to znana granica tej zmiany,
opisana w analizie), zamiast uznawać całość za nieudaną: liczy się też, że stawka z 1996 r.
ZNIKNĘŁA ze źródeł.

Dobrze też przeklikać: „Jaki jest okres wypowiedzenia umowy o pracę?" (kontaminacja 62% przed zmianą).

### 8. Kontrola jawnego wskazania (tor dokładny bez zmian)

Na czacie: „Co mówi ustawa Dz.U. 1996 poz. 110?" — nowela ma się ZNALEŹĆ w źródłach (lane ELI
omija filtr).

## Rollback (bez cofania kodu)

```sql
UPDATE documents SET "AbsorbedAmendment" = false WHERE "AbsorbedAmendment";
```
Filtr przestaje cokolwiek łapać (stan sprzed kroku 4). Reguła 6a promptu zostaje — nieszkodliwa;
pełny rollback promptu = revert commita.

## Stan ustalony (nic do roboty)

`sync-eli` po relinku przelicza flagi automatycznie (log: „Flagi wchłoniętych nowel: zmienionych N").
Świeżo zaingestowana nowela ma default false (zostaje w retrievalu), flaga pojawia się dopiero, gdy
zniknie z list `unabsorbedAmendments`.
