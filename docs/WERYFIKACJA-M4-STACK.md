# Weryfikacja pełnego stacku na M4 — runbook

Jeden uporządkowany przebieg, który potwierdza to, co zostało zbudowane, ale niezweryfikowane na żywym
korpusie: **QU** (retrieval strukturalny), **AKT-0/1** (metadane aktualności), **FE** (interfejs demo).
Infra (Postgres + TEI/Metal + Ollama/Bielik) — jak w [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md) kroki 0–4.

Uwaga: `TemporalAugmenter` (AKT-2 — dołączanie nowel do odpowiedzi) jeszcze NIE istnieje; tu weryfikujemy
tylko, że metadane aktualności są poprawnie utrwalane (fundament pod AKT-2).

---

## Krok 0 — kod + migracje + testy

```bash
git pull origin main
# Migracje: AddDemoConversations (tabele czatu) + AddChunkArticleNo (kolumna + pg_trgm + backfill):
dotnet ef database update --project src/PrawoRAG.Storage
# Pełne testy — z żywym Postgresem powinny przejść też DB-zależne (na słabym laptopie były czerwone):
dotnet test
```
**Sprawdź / zgłoś:** czy `dotnet test` w pełni zielony (na M4 z Postgresem); czy migracje przeszły bez błędu.

## Krok 1 — korpus weryfikacyjny (SZYBKI, kilkanaście aktów)

Do potwierdzenia FUNKCJI nie trzeba pełnych 550k — wystarczą kodeksy z domyślnej listy `Eli:Acts` (zawiera
m.in. KPC `DU/1964/296`, KW `DU/1971/114`, KK `DU/1997/553`). Fetch+embed to minuty.
```bash
Ingestion__Source=ELI Ingestion__Mode=fetch-process dotnet run --project src/PrawoRAG.Ingestion
```
**Sprawdź / zgłoś:** ile aktów przetworzono, `failed=0`, brak wyjątków normalizacji.

## Krok 2 — QU: retrieval strukturalny („co mówi art. X")

```bash
dotnet run --project src/PrawoRAG.Api --no-launch-profile &   # API na :5024 (lub profil)
# 2a. Pytanie-cytat — art. 94 KW powinien być NA GÓRZE (wcześniej: losowe KPC/KK):
curl -s localhost:5024/api/search -H 'content-type: application/json' \
  -d '{"query":"a co z Art. 94 § 2 w zw. z § 1 Kodeksu wykroczeń"}' | jq '.chunks[] | {locator, similarity}'
# 2b. REGRESJA — pytanie pojęciowe ma działać jak dotąd:
curl -s localhost:5024/api/search -H 'content-type: application/json' \
  -d '{"query":"jaka jest kara za jazdę bez ważnego przeglądu"}' | jq '.chunks[0].locator'
```
**Sprawdź / zgłoś:** (2a) czy art. 94 KW jest w wyniku i wysoko; (2b) czy pytanie pojęciowe nie zostało
zepsute (nadal trafne KW). Przetestuj też 2–3 własne cytaty (art. 148 KK, art. 415 KC…).

## Krok 3 — AKT-0/1: metadane aktualności (SQL)

```bash
# consolidatedTextId + unabsorbedAmendments na akcie KPC (po fetchu z Kroku 1):
psql "$PRAWORAG_DB" -c "SELECT \"ExternalId\", \"TypedMetadata\"->>'consolidatedTextId' AS tj,
  jsonb_array_length(COALESCE(\"TypedMetadata\"->'unabsorbedAmendments','[]')) AS nowele
  FROM documents WHERE \"ExternalId\"='DU/1964/296';"
# Podgląd samych nowel niewchłoniętych KPC:
psql "$PRAWORAG_DB" -c "SELECT jsonb_pretty(\"TypedMetadata\"->'unabsorbedAmendments')
  FROM documents WHERE \"ExternalId\"='DU/1964/296';"
```
**Sprawdź / zgłoś:** czy `tj` wskazuje najnowszy tekst jednolity KPC (oczekiwane `DU/2026/468`), oraz czy
lista `unabsorbedAmendments` zawiera nowele ogłoszone po nim (np. `DU/2026/473`, `DU/2026/830`) — a NIE
`DU/2025/1172` (ta jest już wchłonięta). To potwierdza logikę AKT-1 na realnych danych.

## Krok 4 — QU + ArticleNo (backfill/populacja)

```bash
psql "$PRAWORAG_DB" -c "SELECT COUNT(*) FILTER (WHERE \"ArticleNo\" IS NOT NULL) AS z_numerem,
  COUNT(*) AS wszystkie FROM chunks c JOIN documents d ON d.\"Id\"=c.\"DocumentId\" WHERE d.\"DocType\"='act';"
```
**Sprawdź / zgłoś:** czy chunki aktów mają wypełniony `ArticleNo` (powinno być ~100% dla artykułowanych aktów).

## Krok 5 — FE: interfejs demo + Bielik

Uruchom UI z Bielikiem (jak w [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md) — sekcja „Interfejs demo"):
```bash
Llm__Provider=local Llm__Local__Model="SpeakLeash/bielik-11b-v3.0-instruct:Q5_K_M" \
  dotnet run --project src/PrawoRAG.Api
```
Otwórz `http://localhost:5024/` i zadaj m.in.:
- **przypadek prawnika:** pytanie o KPC + o art. z nowelizacji z 1 marca (DU/2025/1172) — czy odpowiada z aktualnego t.j.;
- pytanie-cytat (art. 94 KW) i pytanie pojęciowe.

**Sprawdź / zgłoś:** panel źródeł (cytat, link do ISAP), baner „do weryfikacji", badge cytatów (⚠ przy
Biel-u częściej), streaming, odmowa na pytanie spoza korpusu; czy feedback zapisuje się do tabeli `feedback`.

## Krok 6 — jakość danych (regresje z M4)

```bash
# Bojlerplate formularza (Twój fix) — 0 pozostałości po reprocessingu SAOS (jeśli SAOS w korpusie):
psql "$PRAWORAG_DB" -c "SELECT COUNT(*) FROM chunks WHERE \"Text\" LIKE '%☐%' OR \"Text\" LIKE '%Zwięźle o powodach%';"
```
**Sprawdź / zgłoś:** 0 (jeśli masz orzeczenia apelacyjne karne w bazie).

---

## Co zgłosić z powrotem (zwięźle)

1. Krok 0: `dotnet test` w pełni zielony? migracje OK?
2. Krok 2: „art. 94 KW" wraca art. 94 (i wysoko)? pytania pojęciowe bez regresji?
3. Krok 3: KPC ma `consolidatedTextId=DU/2026/468` i listę niewchłoniętych nowel (473/830, bez 1172)?
4. Krok 4: `ArticleNo` wypełniony na aktach?
5. Krok 5: UI + Bielik odpowiadają z korpusu; źródła/baner/feedback działają? uwagi do UX.
6. Krok 6: 0 bojlerplate?

Po zielonym przebiegu: **(a)** pełny korpus v1 (discovery ON — recepta w
[WERYFIKACJA-PDF-M4.md](WERYFIKACJA-PDF-M4.md)) i/lub **(b)** budowa AKT-2 (TemporalAugmenter) — już z
realną weryfikacją, że nowela dokłada się do odpowiedzi.
