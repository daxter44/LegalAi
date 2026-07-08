# Weryfikacja na M4 — aktualność / nowelizacje (AKT)

Sprawdza JEDYNY otwarty wątek: nowele niewchłonięte do tekstu jednolitego. QU (retrieval strukturalny)
i FE demo są już zweryfikowane — tu ich nie ruszamy. Infra jak w [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md).

Co weryfikujemy: **AKT-0/1** (metadane t.j. + niewchłonięte nowele) oraz **AKT-2/3** (TemporalAugmenter
dokłada nowelę do kontekstu, LLM zestawia stan po zmianie).

---

## Krok 0 — kod + migracje

```bash
git pull origin main
dotnet ef database update --project src/PrawoRAG.Storage   # ArticleNo + pg_trgm + tabele czatu
```

## Krok 1 — korpus weryfikacyjny (KPC + świeże nowele 2026)

Augmenter zadziała tylko, gdy w bazie są ORAZ akt bazowy (KPC), ORAZ dokumenty nowel niewchłoniętych.
Kodeksy są w domyślnym `Eli:Acts`; świeże nowele łapie discovery rocznika 2026:
```bash
# kodeksy (z KPC DU/1964/296):
Ingestion__Source=ELI Ingestion__Mode=fetch-process dotnet run --project src/PrawoRAG.Ingestion
# nowele 2026 (w tym zmieniające KPC, np. DU/2026/473, DU/2026/830):
Ingestion__Source=ELI Ingestion__Mode=fetch-process \
  Eli__Discover__Enabled=true Eli__Discover__YearFrom=2026 Eli__Discover__YearTo=2026 \
    dotnet run --project src/PrawoRAG.Ingestion
```

## Krok 2 — AKT-0/1: metadane niewchłoniętych nowel (SQL)

```bash
psql "$PRAWORAG_DB" -c "SELECT \"TypedMetadata\"->>'consolidatedTextId' AS tj,
  jsonb_pretty(\"TypedMetadata\"->'unabsorbedAmendments') FROM documents WHERE \"ExternalId\"='DU/1964/296';"
```
**Oczekiwane / zgłoś:** `tj = DU/2026/468`; lista niewchłoniętych zawiera nowele ogłoszone po nim
(np. `DU/2026/473`, `DU/2026/830`), a NIE `DU/2025/1172` (ta jest już w kwietniowym t.j.).

## Krok 3 — który artykuł KPC zmienia świeża nowela (do ułożenia pytania)

```bash
# szukamy w chunkach nowel odwołań do artykułów KPC (diff: „w art. N …"):
psql "$PRAWORAG_DB" -c "SELECT d.\"ExternalId\", substring(c.\"Text\" for 200) FROM chunks c
  JOIN documents d ON d.\"Id\"=c.\"DocumentId\"
  WHERE d.\"ExternalId\" IN ('DU/2026/473','DU/2026/830') AND c.\"Text\" ~ 'art\\.\\s*[0-9]' LIMIT 5;"
```
**Zgłoś:** który artykuł KPC (np. „art. 367") wymienia któraś z nowel — użyj go w Kroku 4.

## Krok 4 — AKT-2/3 end-to-end (to jest sedno)

```bash
dotnet run --project src/PrawoRAG.Api --no-launch-profile &   # lub UI z Bielikiem (URUCHOMIENIE-M4)
# podstaw artykuł z Kroku 3 (przykład: 367):
curl -s localhost:5024/api/chat -N -H 'content-type: application/json' \
  -d '{"question":"co mówi art. 367 Kodeksu postępowania cywilnego?"}'
```
Albo przez UI (`http://localhost:5024/`) — to samo pytanie.

**Oczekiwane / zgłoś:**
- w zdarzeniu `sources` jest fragment z markerem **„[NOWELIZACJA — obowiązuje od …]"** (dołożony przez augmenter),
- odpowiedź LLM **zestawia** stan z tekstu jednolitego i zmianę z noweli, wskazuje **od kiedy**, cytuje oba,
- panel źródeł w UI pokazuje nowelę obok tekstu jednolitego.

## Krok 5 — regresja (brak fałszywych nowel)

Zapytaj o artykuł, którego żadna świeża nowela nie zmienia (np. dobrze ugruntowany art. z KK):
```bash
curl -s localhost:5024/api/chat -N -H 'content-type: application/json' \
  -d '{"question":"co mówi art. 148 Kodeksu karnego?"}'
```
**Oczekiwane / zgłoś:** BRAK markera „[NOWELIZACJA…]" — augmenter nic nie dokłada, zachowanie jak dotąd.

---

## Co zgłosić z powrotem

1. Krok 2: KPC ma `consolidatedTextId=DU/2026/468` i listę niewchłoniętych (473/830, bez 1172)?
2. Krok 4: nowela dołączona do źródeł (marker) i LLM zestawił stan po zmianie z datą?
3. Krok 5: brak nowel tam, gdzie ich nie ma (zero fałszywych dołączeń)?
4. Uwagi do treści zestawienia (czy Bielik poprawnie łączy stary tekst + zmianę — czy myli).

Augmentacja działa w OBU torach — UI (`ChatService`) i endpoint SSE (`/api/chat`) — więc weryfikacja
przez `curl` i przez przeglądarkę daje ten sam wynik.
