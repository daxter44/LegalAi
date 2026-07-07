# Weryfikacja ścieżki PDF (teksty jednolite) — zadanie dla agenta na M4

Handoff dla maszyny z pełnym stackiem (TEI/Metal + Postgres + Ollama). Ten dokument mówi **co i jak
sprawdzić**, żeby potwierdzić, że nowa ścieżka PDF działa nie tylko na Kodeksie karnym.

## Kontekst — co się zmieniło i dlaczego (przeczytaj najpierw)

**Problem.** ELI (api.sejm.gov.pl) **przestał publikować HTML w styczniu 2025**. Zweryfikowane empirycznie:
2024 = 1189/1189 aktów z HTML, 2025 = 0/1900, 2026 = 0/899. Co gorsza, `text.html` serwowany dla
kodeksów jest **przestarzały**: art. 37 KK w HTML brzmiał „najdłużej **15 lat**" — to stan sprzed reformy
z 1 X 2023 (która podniosła max do **30 lat**). Aktualny tekst jednolity każdego kodeksu istnieje już
**tylko jako PDF** (najnowsze obwieszczenia mają `textHTML=false`).

**Definicje (bez żargonu).**
- *Tekst jednolity (t.j.)* — pełny, aktualny tekst ustawy z wpisanymi wszystkimi nowelizacjami; to jest
  tekst, który prawnik faktycznie cytuje. Publikowany jako *obwieszczenie Marszałka Sejmu*.
- *born-PDF* — akt, który od 2025 istnieje wyłącznie jako PDF (nigdy nie miał HTML).
- *born-digital PDF* — PDF wygenerowany z tekstu (ma warstwę tekstową), a nie skan; da się z niego
  wyciągnąć tekst bez OCR.

**Rozwiązanie (na `main`, 4 commity „feat(pdf)…" + „feat(eli)…").**
1. `PdfPigTextExtractor` — wyciąga tekst z PDF (PdfPig, czysto zarządzany, bez OCR).
   `RawDocument.ContentFormat` = `html` | `pdf-text` steruje ścieżką parsowania.
2. `ActTextParser` — segmenter z płaskiego tekstu: dzieli po `Art.`/`§`, **usuwa nagłówki stron**
   („Dziennik Ustaw – N – Poz. M"), **pomija preambułę obwieszczenia** (od „Załącznik do obwieszczenia"
   → pierwszy `Art.`/`§`), pomija jednostki „(uchylony)". Punkty (1) 2) …) zostają inline (świadome v1).
3. `EliSejmConnector` — dla każdego aktu rozwiązuje treść do **najnowszego t.j.** (HTML gdy jest, inaczej
   PDF); tożsamość dokumentu (`ExternalId`, tytuł, metadane) zostaje **bazowa** — prawnik cytuje „KK"
   (DU/1997/553), nie obwieszczenie.
4. Discovery **bez wymogu HTML** → wchodzą born-PDF 2025+. Pełny zakres 1994–2026 = **13 988 aktów**.

**Co już potwierdzono na słabym laptopie (bez GPU/DB):** 30/30 czystych testów; `fetch` KK →
`ContentFormat=pdf-text`, treść z t.j. DU/2025/383, **„najdłużej 30 lat"**, zero „15 lat".
**Czego TU nie dało się sprawdzić** (brak TEI/Postgres/GPU) i co należy do Ciebie na M4:
pełny `process`→embed→retrieval oraz **różnorodność typów** (walidowano głównie KK).

---

## A. Raport jakości na MIESZANYCH typach — CPU, za darmo, bez DB/GPU (NAJWAŻNIEJSZE)

Cel: dowód, że segmenter radzi sobie z kodeksem, zwykłą ustawą, rozporządzeniem **oraz born-PDF 2025**
(nie-kodeksem). `report` czyta z magazynu surowych — więc najpierw `fetch`, potem `report`.

```bash
# 1a. Kodeksy/ustawy z listy Eli:Acts (KK, KC, KPK… — mają t.j., connector weźmie najnowszy PDF/HTML):
Ingestion__Source=ELI Ingestion__Mode=fetch dotnet run --project src/PrawoRAG.Ingestion

# 1b. Próbka born-PDF 2025 (discovery tylko 2025; discovered idą PIERWSZE, więc MaxItems łapie born-PDF):
Ingestion__Source=ELI Ingestion__Mode=fetch Ingestion__MaxItems=25 \
  Eli__Discover__Enabled=true Eli__Discover__YearFrom=2025 Eli__Discover__YearTo=2025 \
  dotnet run --project src/PrawoRAG.Ingestion

# 2. Raport (CPU, bez bazy/embeddingu) — normalizuje wszystko z magazynu i wypisuje statystyki + próbki:
Ingestion__Source=ELI Ingestion__Mode=report dotnet run --project src/PrawoRAG.Ingestion
```

**Na co patrzeć w raporcie:**
- Podsumowanie: **`bez_segmentów=0` i `błędy=0`** (każdy dokument dał segmenty).
- „próbka 1. segmentu" — **NIE** może zawierać:
  - `Dziennik Ustaw – N – Poz. M` (nagłówki stron → powinny być odsiane),
  - `OBWIESZCZENIE MARSZAŁKA` / `Na podstawie art. 16` (preambuła obwieszczenia → odsiana).
- Etykiety segmentów sensowne: `Art. N` / `Art. N § M` (ustawy, kodeksy), `§ N` (rozporządzenia).

**RYZYKO do świadomego potwierdzenia (najważniejszy powód tego kroku):**
segmenter pomija preambułę od markera „Załącznik do obwieszczenia". Akt born-PDF publikowany **wprost**
(nie jako obwieszczenie t.j.) tego markera **nie ma** → `SkipPreamble` spada na pierwszy `Art.`/`§`.
Obejrzyj próbkę takiego aktu i sprawdź, czy **tytuł/podstawa prawna z początku** nie wylądowały jako
treść „Art. 1". Jeśli tak — to defekt do poprawy w `ActTextParser` (zgłoś z `ExternalId`).

---

## B. Aktualność end-to-end — wymaga TEI (Metal) + Postgres

Cel: dowód, że aktualna treść dochodzi aż do retrievalu. Infra jak w [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md) (kroki 3–4).

```bash
# fetch+process KK (Eli:Acts zawiera DU/1997/553 = Kodeks karny):
Ingestion__Source=ELI Ingestion__Mode=fetch-process dotnet run --project src/PrawoRAG.Ingestion

# API i zapytanie o przepis zmieniony reformą X 2023 (max kara pozbawienia wolności):
dotnet run --project src/PrawoRAG.Api --no-launch-profile   # (patrz URUCHOMIENIE-M4 krok 6)
curl -s localhost:5024/api/search -H 'content-type: application/json' \
  -d '{"query":"maksymalny wymiar terminowej kary pozbawienia wolności"}' | jq
```
**Dowód aktualności:** wśród zwróconych fragmentów ma być **„30 lat"** (aktualne), a **nie „15 lat"**
(stary HTML). Dodatkowo w magazynie: `data/raw/ELI/DU_1997_553.json` → `"ContentFormat": "pdf-text"`.

---

## C. Odporność masowego born-PDF + kwarantanna — wymaga `process`

```bash
# Potwierdź wolumen pełnego zakresu (ma być ~13 988):
Ingestion__Source=ELI Ingestion__Mode=discover Eli__Discover__Enabled=true Eli__Discover__YearTo=2026 \
  dotnet run --project src/PrawoRAG.Ingestion

# fetch+process próbki born-PDF 2025 (~50) i policz błędy/kwarantannę:
Ingestion__Source=ELI Ingestion__Mode=fetch-process Ingestion__MaxItems=50 \
  Eli__Discover__Enabled=true Eli__Discover__YearFrom=2025 Eli__Discover__YearTo=2025 \
  dotnet run --project src/PrawoRAG.Ingestion
```
**Na co patrzeć:** w logu `process` — `failed=?`. W bazie: dokumenty ze `Status=Failed` = kwarantanna
(np. PDF-skan bez warstwy tekstowej → ekstraktor rzuca „PDF bez warstwy tekstowej"). Policz ile i podaj
przykłady — to kandydaci do OCR (poza obecnym zakresem, świadomie).

---

## Znalezione i NAPRAWIONE (weryfikacja M4, 2026-07)

Ścieżka PDF zaliczona: A (40/40 dok. dały segmenty, 0 błędów/issues, w tym 25 born-PDF; ryzyko preambuły
nie wystąpiło), B (KK art. 37 = „30 lat"), C (kwarantanna 0/40).

**Bug w ścieżce HTML (naprawiony, commit `fix(eli): pomiń przepisy CYTOWANE…`).** Obwieszczenie t.j. w HTML
ma sekcję „Treść obwieszczenia" cytującą przepisy ustaw nowelizujących (klasa `pro-cite-text`) o tych
samych numerach co prawdziwe artykuły z załącznika → oba trafiały do bazy pod jednym numerem.
Zmierzone kolizje: KSH 8, ustrój sądów powszechnych 9, obrót instrumentami 12. Fix: `ActNormalizer` bierze
tylko `pro-text`. Regresja w testach.

**Do re-weryfikacji na M4:** przetwórz ponownie te 3 akty i potwierdź, że fałszywe fragmenty zniknęły:
```bash
# fetch+process KSH (DU/2000/1037), ustrój sądów powszechnych, obrót instrumentami
# — dodaj ich adresy do Eli:Acts albo pobierz przez discovery, potem:
Ingestion__Source=ELI Ingestion__Mode=fetch-process dotnet run --project src/PrawoRAG.Ingestion
# W bazie: dla żadnego artykułu nie powinno być 2 chunków z tym samym numerem, gdzie jeden to
# klauzula „Ustawa wchodzi w życie…". Szybki test: /api/search o „zgłoszenie spółki partnerskiej"
# ma zwrócić realny art. 93 KSH, nie klauzulę wejścia w życie.
```

## Co zgłosić z powrotem (zwięźle)
1. Raport (A): czy `bez_segmentów=0` i `błędy=0` na mieszanych typach? Które typy sprawdzone.
2. Born-PDF nie-obwieszczenie: czy preambuła jest poprawnie pomijana (albo `ExternalId` defektu)?
3. Retrieval KK (B): czy zwraca „30 lat" (aktualne), nie „15 lat"?
4. Kwarantanna (C): ile aktów nieekstrahowalnych (PDF-skan) na próbce i przykłady.

Kod ścieżki PDF: `src/PrawoRAG.Ingestion/Pdf/`, `Eli/ActTextParser.cs`, `Eli/EliSejmConnector.cs`
(`NewestConsolidatedText`, `FetchActAsync`). Testy: `tests/PrawoRAG.Tests/Ingestion/{PdfTextExtractor,
ActTextParser,EliConsolidatedText,EliDiscovery}Tests.cs`.
