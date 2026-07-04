# Plan: AI dla prawników — RAG z ugruntowaniem w źródłach (PrawoRAG)

## Context

Prawnicy nie mogą ufać AI, bo modele halucynują — wymyślają paragrafy i nieprecyzyjnie cytują, wprowadzając w błąd. **Rdzeniem produktu nie jest "RAG" sam w sobie, tylko zachowanie: model odpowiada wyłącznie na podstawie zacytowanych, weryfikowalnych źródeł, a gdy ich brak — jawnie mówi „nie mam wystarczającej wiedzy" zamiast zmyślać.** To jest wartość, za którą kancelaria zapłaci.

POC (na innej maszynie, nie w tym repo) potwierdził tezę: RAG + Bielik 11B dawał odpowiedzi jakościowo zbliżone do Claude Sonnet **na dobrze wyszukanej treści**. Wniosek: dźwignią jakości jest **jakość retrieval + ugruntowanie**, a nie rozmiar modelu — model traktujemy jako wymienny komponent.

Cel: zbudować MVP, które zdobędzie zaufanie kancelarii i zweryfikuje działanie, przy **minimalnych kosztach utrzymania** (solo, po godzinach, bez budżetu), z architekturą gotową na rozszerzanie (nowe źródła, nowe typy dokumentów) i na późniejsze podłączenie modeli lokalnych (pakiet Diamond).

### Decyzje podjęte z użytkownikiem
- **LLM w MVP:** cloud API (Claude/OpenAI), warstwa LLM abstrakcyjna i wymienna. Bielik wraca później jako **pakiet Diamond** — wdrażany lokalnie u klienta na **Mac mini** (dane nie opuszczają kancelarii).
- **Hosting infra:** tani VPS (Hetzner / Mikr.us), bez GPU. **Sizing: 8 GB RAM** (Hetzner CPX21/CX32, ~7–8 €/mc) — domyślne 4 GB nie udźwignie Postgres + TEI + API + worker (patrz „Budżet pamięci i sizing VPS").
- **Stack:** **.NET / C#** (strefa komfortu użytkownika; wcześniejsza „powolność" wynikała z inference modeli, nie z języka). Komponenty ML, które istnieją tylko w Pythonie, dołączamy później jako osobne sidecary; w MVP unikamy Pythona.

---

## Cel MVP (zakres) i granice

**W MVP (must-have):**
1. Worker ingestii: pobiera dokumenty ze źródeł → normalizuje → chunkuje → embeduje → zapisuje do bazy wektorowej z metadanymi. Wspiera **przyrostową synchronizację** (od daty modyfikacji).
2. Baza wektorowa + metadane + wyszukiwanie hybrydowe (semantyczne + słowa kluczowe/numery artykułów i sygnatur).
3. API retrieval + chat (streaming).
4. Cienki frontend w stylu ChatGPT.
5. **Ugruntowanie i abstynencja (rdzeń):** każda teza w odpowiedzi ma cytat z lokalizatorem źródła; gdy retrieval ma niską pewność — odpowiedź „nie mam wystarczających źródeł". Snippety źródeł widoczne w UI do weryfikacji przez prawnika.
6. Źródła w MVP: **SAOS** (orzeczenia) + **ISAP via ELI/Sejm API** (akty prawne).

**Poza MVP (roadmap, w tej kolejności):**
- Reranker (poprawa precyzji retrieval).
- Kolejne źródła: EUR-Lex, Trybunał Konstytucyjny, interpretacje podatkowe — *dowód rozszerzalności domeny*.
- Aplikacja desktop: PDF→Markdown (Docling) + lokalna anonimizacja PII (Presidio + polski NER) przed wysłaniem do chmury.
- **Pakiet Diamond:** lokalny Bielik na Mac mini u klienta.

---

## Architektura (separacja: worker / domena / infra+API)

Rozwiązanie monorepo, solucja .NET z projektami odpowiadającymi Twojej dekompozycji:

```
PrawoRAG.sln
├── src/
│   ├── PrawoRAG.Domain/          # rdzeń domenowy — kontrakty rozszerzalności, modele, schemat metadanych
│   ├── PrawoRAG.Ingestion/       # Worker Service (BackgroundService): fetch → normalize → chunk → embed → upsert
│   ├── PrawoRAG.Embeddings/      # klient HTTP do serwisu embeddingów (HF TEI)
│   ├── PrawoRAG.Llm/             # abstrakcja LLM (ILlmProvider): Claude / OpenAI / (później) Bielik
│   ├── PrawoRAG.Storage/         # repozytorium pgvector/Postgres (Npgsql + Pgvector)
│   ├── PrawoRAG.Api/             # ASP.NET Core Web API: chat (SSE/streaming), retrieval, auth
│   └── PrawoRAG.Web/             # frontend chat (Blazor — domyślnie; React/Next jako alternatywa)
├── infra/
│   └── compose.yaml              # postgres+pgvector, TEI (embeddingi), api, worker
└── tests/
```

**Konteneryzacja: Podman** (nie Docker). Na maszynie dev: Podman 5.7.1 + plugin `podman compose` (czyta `compose.yaml`/`docker-compose.yml` tak samo jak Docker). Implikacje:
- Komendy: `podman compose up/down` zamiast `docker compose`. Obrazy z `docker.io`/`ghcr.io` działają bez zmian.
- **Rootless domyślnie** — porty <1024 wymagają mapowania na wyższe; wolumeny pod kątem uprawnień (`:Z` dla SELinux na Linuksie; na WSL nieistotne).
- **Testcontainers (.NET):** współpracuje z Podmanem, ale wymaga wskazania socketu — ustawić `DOCKER_HOST=unix:///run/user/$UID/podman/podman.sock` (Linux) lub `npipe` na Windows, oraz `TESTCONTAINERS_RYUK_DISABLED=true` jeśli Ryuk nie wstaje na rootless. Udokumentować w README testów.
- **Maszyna Podman (WSL) ma obecnie 2 GiB RAM — za mało na TEI + Postgres naraz.** Przed 0.2: `podman machine stop; podman machine set --memory 8192 --cpus 6; podman machine start` (lub recreate). Produkcyjny VPS Hetzner: Podman natywnie na Linuksie, bez maszyny.

**Komponenty infrastruktury (kontenery, zero kodu do utrzymania):**
- **Postgres + pgvector** — jedna baza na: wektory, metadane, użytkowników/subskrypcje, historię czatu, polski full-text search (BM25 przez `tsvector`). Jedna zależność, jeden backup → minimalny ops dla solo dev.
- **HF Text Embeddings Inference (TEI)** — gotowy kontener (Rust), serwuje model **mmlw-retrieval-roberta** przez HTTP. .NET woła go przez REST. Batching + szybkie CPU inference; rozwiązuje wcześniejszą „powolność".
- **LLM** — cloud API (HTTP) za interfejsem `ILlmProvider`.

### Rozszerzalność domeny (wymaganie b)
Dwa kontrakty w `PrawoRAG.Domain`:
- `ISourceConnector` — pobiera nowe/zmienione dokumenty ze źródła od podanej daty (`sinceModificationDate`), zwraca surowe dokumenty + metadane źródłowe. Implementacje: `SaosConnector`, `EliSejmConnector`, (później) `EurLexConnector`.
- `IDocumentNormalizer` — surowy dokument → znormalizowany tekst + ustrukturyzowane metadane + chunki. Implementacje per typ dokumentu: orzeczenie, akt prawny, (później) artykuł prawniczy, książka OCR (.md).

**Dodanie nowego źródła = nowy `ISourceConnector` + rejestracja w DI.** Dodanie nowego typu dokumentu = nowy `IDocumentNormalizer`. To realizuje wymóg łatwego rozszerzania.

Analogicznie do `ILlmProvider`, embedding chowamy za **`IEmbeddingProvider`** (implementacje: `TeiEmbeddingProvider`, później ew. `CloudEmbeddingProvider`). Dzięki temu zmiana modelu/dostawcy embeddingów (self-hosted ↔ cloud, base ↔ large) nie wymaga zmian w workerze ani API.

### Ingestia masowa (batch na GPU) vs przyrostowa — i wybór modelu na życie korpusu
**Problem:** SAOS to ~600k orzeczeń (długich) → po chunkowaniu ~6 mln chunków. Lokalny embedding na CPU bez batchingu (~1000 dok/h zaobserwowane) = ~25 dni. Nie do przyjęcia.

**Rozwiązanie — rozdziel dwa tryby:**
- **Masowy (jednorazowy/rzadki):** embedding całego korpusu jako **batch na wynajętym GPU** (RunPod/Vast.ai, RTX 4090 ~0,3–0,5 $/h), TEI z batchingiem (~1000–5000+ chunk/s). Cały korpus 600k = **~kilka godzin, kilka dolarów**, nie tygodnie. Wynik (wektory + metadane) ładujemy do pgvector. **Nie na produkcyjnym VPS.**
- **Przyrostowy (ciągły):** nowe/zmienione orzeczenia (sync dzienny, `sinceModificationDate`) → drobny wolumen → CPU na VPS w zupełności wystarcza. Dedup po `content_hash` — nie embedujemy niezmienionych.

**Lock modelu embeddingów (krytyczne):** zapytanie i baza MUSZĄ być przetworzone tym samym modelem. Zmiana modelu = **re-embedding całego korpusu**. Dlatego:
- **Wybór base vs large-v2 RAZ, przed masową ingestią**, na podstawie szybkiego A/B na próbce SAOS (jakość na realnych pytaniach prawnych).
- Skoro masowy embedding idzie na GPU, RAM VPS nie ogranicza wyboru modelu — ogranicza go tylko **serwowanie zapytań** (jeden mały model w pamięci). `large-v2` jako serwer zapytań ≈ 2–2,5 GB → mieści się na 8 GB VPS. **Więc można od razu wziąć `large-v2`, jeśli A/B pokaże wyraźnie lepszą jakość — bez kary „przeembeduj później".**
- Zapisuj `embedding_model` + wersję w metadanych każdego wektora; nigdy nie mieszaj modeli w jednym indeksie.

**Zakres korpusu na MVP:** do walidacji wartości **nie ingestujemy od razu 600k**. Bierzemy sensowny wycinek (np. jedna dziedzina prawa / ostatnie lata / wybrane sądy), dowodzimy jakości i ugruntowania, a pełny korpus dociągamy gdy pipeline + batch GPU są sprawdzone.

### Embedding zapytań na CPU — dlaczego VPS to udźwignie
Embedding promptu użytkownika to **jeden chunk = jeden forward pass**, nie miliony jak przy korpusie. Latencja na CPU (TEI, fp16): **`base` ~50–200 ms, `large-v2` ~150–500 ms na zapytanie** — niezauważalne przy generowaniu LLM rzędu 2–10 s. Model ładuje się do RAM **raz przy starcie**, siedzi rezydentnie. Obserwowane „1000 dok/h" było wolne przez *przemnożenie przez ~6 mln chunków* + I/O + normalizacja, nie przez pojedynczą operację (pojedynczy chunk = kilkaset ms).

**Realny limit to współbieżność** (wielu userów naraz), nie pojedyncza latencja. Na MVP nieistotne (TEI kolejkuje/batchuje). Ścieżka eskalacji bez przepisywania (dzięki `IEmbeddingProvider`): dedykowane vCPU (Hetzner CCX) → wydzielony serwis TEI na osobnym boxie → GPU dopiero przy realnym ruchu/przychodach.

**Pułapka:** korpus embedowany mmlw ⇒ zapytania MUSZĄ iść tym samym mmlw. „Cloud embedding tylko dla zapytań" jest **niedozwolony** (chyba że cały korpus też przeembedujesz tym samym modelem cloudowym). Query embedding zostaje self-hosted na modelu korpusu.

### Budżet pamięci i sizing VPS (serwowanie, nie ingestia masowa)
VPS obsługuje Postgres + API + embedding **zapytań** + przyrost. Szacunek RAM (CPU, fp16):

| Komponent | RAM |
|---|---|
| OS | ~0,4 GB |
| Postgres + pgvector (budowa indeksu spike'uje) | ~0,5–1 GB |
| TEI zapytań — `mmlw-base` ~1–1,5 GB / `large-v2` ~2–2,5 GB | 1–2,5 GB |
| .NET API | ~0,3 GB |

**Rekomendacja:** Hetzner **CPX21/CX32 (8 GB, ~7–8 €/mc)** — pomieści nawet `large-v2` przy serwowaniu zapytań. Domyślne 4 GB odpada. **Tuning Postgres:** ogranicz `maintenance_work_mem` przy budowie indeksu (lub IVFFlat zamiast HNSW — lżejszy), `shared_buffers` nisko, indeks buduj po załadowaniu korpusu.

---

## Stack techniczny (rekomendacje z uzasadnieniem)

> Uwaga: konkretne wersje/benchmarki modeli **zweryfikować na HuggingFace na etapie budowy** — research dał wartości orientacyjne, część precyzyjnych liczb może być niedokładna.

| Rola | Wybór MVP | Uzasadnienie / licencja | Uwagi |
|---|---|---|---|
| Orkiestracja (API/worker/domena) | **.NET / C#** | Strefa komfortu → velocity solo dev | Wąskie gardło to inference, nie język |
| Embedding | **mmlw-retrieval-roberta** (base lub large-v2 — **wybór RAZ przed masową ingestią**, patrz niżej) przez **TEI**, za `IEmbeddingProvider` | Apache 2.0, najlepszy na polski; TEI = bez kodu Python | **Wymaga prefiksu `zapytanie: ` dla zapytań**, brak prefiksu dla pasaży. Limit ~512 tokenów → chunking. **Model zablokowany na życie korpusu** (zapytanie i baza muszą być tym samym modelem). `embedding_model`+wersja w metadanych |
| Baza wektorowa | **pgvector (Postgres)** | Jedna baza na wszystko (wektory+metadane+użytkownicy+FTS); min. ops; dobre wsparcie .NET (Npgsql) | Qdrant = opcja przy skali/zaawansowanym hybrydzie (później) |
| Wyszukiwanie | **Hybrydowe**: dense (mmlw) + BM25 (`tsvector`, konfiguracja polska), fuzja RRF | Numery artykułów/sygnatury to sygnał „rzadki" — sam dense je gubi | Kluczowe dla prawa |
| LLM (MVP) | **Claude / OpenAI** za `ILlmProvider` | Zero kosztu idle, grosze przy małym ruchu | Najnowsze modele Claude (rodzina 4.x) |
| LLM (Diamond, później) | **Bielik 11B** (rodzina v2.x — zweryfikować wersję/kontekst) | Apache 2.0; OSS, polski, prywatność | Lokalnie na Mac mini (Ollama/llama.cpp, kwantyzacja GGUF) |
| Frontend | **Blazor** (domyślnie) | Jeden ekosystem z backendem | React/Next jako alternatywa jeśli wolisz |
| Reranker (później) | sdadas/polish-reranker-* lub bge-reranker-v2-m3 | +precyzja@1 dla prawa | Sprawdzić licencję Gemma vs MIT |
| PDF→MD (desktop, później) | **Docling** (IBM) | **MIT** — czysta licencja komercyjna | Marker/PyMuPDF4LLM/MinerU = GPL/AGPL → unikać lub jako osobny serwis |
| Anonimizacja PII (desktop, później) | **Presidio + spaCy `pl_core_news_lg`** | Apache 2.0 / MIT | Polski NER lokalnie |

---

## Źródła danych (research)

### MVP — najniższy nakład, najwyższa wartość
1. **SAOS** — `https://www.saos.org.pl/api/` — **REST API, JSON, bez auth**. Search API + Dump API (bulk) + przyrostowa synchronizacja przez `sinceModificationDate`. Orzeczenia sądów powszechnych, SN, administracyjnych, TK (2004+). **Łatwość: ŁATWA.**
2. **ISAP via ELI/Sejm API** — `https://api.sejm.gov.pl/eli` — **REST API, JSON, OpenAPI/Swagger**. Dziennik Ustaw + Monitor Polski, akty z tekstami. Struktura ELI (European Legislation Identifier) → idealne lokalizatory cytatów. **Łatwość: ŚREDNIA.**

### Roadmap — dowód rozszerzalności domeny
- **EUR-Lex** — SPARQL (CELLAR) + bulk RDF; **najczystsza licencja: CC-BY-4.0 (treść) + CC0 (metadane)**. Prawo UE. Łatwość: średnia/trudna.
- **Trybunał Konstytucyjny** — SAOS ma TK tylko do ~2015 (zamrożone); dla bieżącego orzecznictwa TK potrzebny inny kanał (ipo.trybunal.gov.pl). SAOS przydatny tylko do historii.
- **dane.gov.pl** — REST API; głównie metadane sądów (adresy), mało orzecznictwa.
- **EUREKA (interpretacje podatkowe)**, **orzeczenia.ms.gov.pl** — tylko scraping, brak API → niski priorytet.

---

## Zweryfikowana struktura źródeł — specyfikacja normalizacji i chunkowania (sprawdzone na żywych API, 2026-06-11)

### SAOS — trzy endpointy, różne reprezentacje
| Endpoint | Zwraca | Użycie |
|---|---|---|
| `GET /api/search/judgments` | JSON, paginacja (`pageSize` 1–100), `textContent` **obcięty czysty tekst** (~800 znaków) | Tylko odkrywanie ID / filtrowanie wycinka. **NIE do ingestu treści** |
| `GET /api/judgments/{id}` | Pełny dokument, `textContent` = **HTML** | Pobranie pojedynczego dokumentu |
| `GET /api/dump/judgments?pageSize=10..100&sinceModificationDate=yyyy-MM-ddTHH:mm:ss.SSS` | Strony pełnych dokumentów | **Podstawa ingestu masowego i przyrostowego** (zweryfikowane: działa) |

**Pola pełnego dokumentu (zweryfikowane):** `id`, `courtType` (COMMON / SUPREME / CONSTITUTIONAL_TRIBUNAL / NATIONAL_APPEAL_CHAMBER / ADMINISTRATIVE), `courtCases[].caseNumber` (sygnatura), `judgmentType` (DECISION / RESOLUTION / SENTENCE / REGULATION / REASONS), `judgmentDate`, `judges[]` (name, function, specialRoles), `source` (judgmentUrl do oryginału, publicationDate), `courtReporters`, `decision`, `summary`, `textContent` (HTML), `legalBases`, **`referencedRegulations[]`** (journalTitle, journalNo, journalYear, journalEntry + `text` z **listą artykułów**!), `keywords`, `referencedCourtCases`, `division` (wydział + sąd z nazwą i kodem). Wolumen całości: **536 802** orzeczeń (stan 2026-06).

**HTML `textContent`:** `<p>`, `<h2>`, `<strong>`, komentarze `<!-- -->` oraz **`<span class="anon-block">`** — dane osobowe już zanonimizowane u źródła (np. „R. I.", „ul. (...)"). Zachowywać jako tekst.

**ŚWIEŻOŚĆ DANYCH per typ sądu (zweryfikowane 2026-06-11 — krytyczne dla doboru korpusu):**
| Typ sądu | Najnowsze orzeczenie | Wolumen | Status |
|---|---|---|---|
| **COMMON (powszechne)** | **2026-06-08** | 467 050 | **aktualizowany na bieżąco** ✅ |
| SUPREME (SN) | ~2016-06 | 38 081 | zamrożony |
| CONSTITUTIONAL_TRIBUNAL | ~2015-12 | 9 503 | zamrożony |
| NATIONAL_APPEAL_CHAMBER (KIO) | ~2018-09 | 22 168 | zamrożony |
| ADMINISTRATIVE | — | 0 | niedostępny w SAOS |

**Konsekwencja:** jedyny strumień z aktualnym orzecznictwem to **sądy powszechne (COMMON)**. SN/TK/KIO w SAOS są historyczne (wartościowe jurydycznie, ale bez bieżącego orzecznictwa) — to **koryguje wcześniejsze założenie**, że TK/NSA „przez SAOS" dadzą świeże dane. NSA/WSA trzeba będzie wziąć z innego źródła (CBOSA — scraping) jeśli potrzebne. Rozkład COMMON 2021–06.2026: apelacyjne 16 081, okręgowe 53 004, rejonowe 31 711.

**Stwierdzone problemy jakości danych (realne przykłady z API):**
- `judgmentDate` z literówkami źródłowymi: `3013-12-04`, `2101-04-14` (w treści: 2013, 2016);
- sklejone nazwiska sędziów: „Małgorzata Stanek Sędziowiehanna Rojewska";
- `legalBases` bywa puste; `referencedRegulations` bywa nadmiarowe (np. Kodeks pracy przypisany do sprawy karnej);
→ **normalizer waliduje daty** (zakres 1990–dziś; poza zakresem: próba ekstrakcji daty z treści, inaczej flaga `quality_issue`), metadane `judges`/`keywords` traktuje jako best-effort, **nie odrzuca dokumentu** z powodu błędnych metadanych.

**Normalizacja orzeczenia (`JudgmentNormalizer`):**
1. HTML→tekst (HtmlAgilityPack): akapity jako `\n\n`, usunięte komentarze, `anon-block` zachowany jako tekst.
2. Detekcja sekcji po nagłówkach/wzorcach: **komparycja** (nagłówek z sygnaturą/składem), **sentencja** (od „WYROK"/„POSTANOWIENIE"/„UCHWAŁA"), **uzasadnienie** (od „UZASADNIENIE") → `section` w metadanych chunka.
3. Metadane: sygnatura z `courtCases[0]`, sąd/wydział z `division`, data po walidacji, `referencedRegulations` sparsowane do (rok, nr, pozycja, [artykuły]) — **most do aktów ELI** (przyszłe linkowanie orzeczenie↔akt).
4. `content_hash` = SHA-256 surowego `textContent`.

**Chunking orzeczenia:** cel ~450 tokenów, overlap ~80, twardy limit 512 (tokenizer zgodny z modelem — TEI ma endpoint `/tokenize`, używać go zamiast przybliżeń znakowych). Granice na akapitach, bez przecinania zdań. Metadane chunka: `section`, `chunk_index`, `char_start/end`.

### ELI/Sejm — akty prawne
| Endpoint | Zwraca |
|---|---|
| `GET /eli/acts/DU/{year}` | lista aktów rocznika |
| `GET /eli/acts/DU/{year}/{pos}` | metadane aktu (JSON) |
| `GET /eli/acts/DU/{year}/{pos}/text.html` | **pełny tekst ujednolicony, strukturalny HTML** (KK: ~1 MB) |

**Metadane aktu (zweryfikowane na KK, DU/1997/553):** `address` (WDU19970880553), `displayAddress` („Dz.U. 1997 nr 88 poz. 553"), `title`, `type` (Ustawa), `status` („akt posiada tekst jednolity"), **`inForce`** (IN_FORCE) , `entryIntoForce`, **`changeDate`** (timestamp ostatniej zmiany → **sync przyrostowy**), `keywords`, `references` („Akty zmieniające" z datami wejścia w życie, „Tekst jednolity", …), `textHTML`/`textPDF`.

**Struktura `text.html` (zweryfikowana):**
- wbudowane **drzewo JSON struktury** aktu: hierarchia `chpt` (rozdział) → `arti` (artykuł) → `para` (paragraf) → `pint` (punkt), z `id`/`symbol`/`title`;
- body: `<div class="unit unit_arti" id="none_-chpt_XIX-arti_148" data-id="arti_148">` zawiera `<h3>Art. 148.</h3>` + zagnieżdżone `<div class="unit unit_para" data-id="para_1">` z tekstem w `<div data-template="xText">`.
→ **Parsowanie artykułów jest deterministyczne** (po `div.unit_*`), bez heurystyk.

**Normalizacja aktu (`ActNormalizer`):**
1. Parsuj drzewo struktury (lub `div.unit_*`) → hierarchia rozdział→artykuł→paragraf→punkt.
2. **Chunk bazowy = artykuł**, z nagłówkiem kontekstowym: *„Kodeks karny, Rozdział XIX – Przestępstwa przeciwko życiu i zdrowiu, Art. 148"*. Artykuł > limitu tokenów → dziel po paragrafach, każdy z powtórzonym nagłówkiem.
3. Lokalizator cytatu: `eli_id` (DU/1997/553) + `article` (+ `paragraph`); link: `…/text.html#none_-chpt_XIX-arti_148` + `displayAddress` dla człowieka.
4. `in_force` z metadanych; `changeDate` → `source_modification_date`; `content_hash` z `text.html`.

**Do zweryfikowania przy implementacji (1 sprawdzenie):** czy `text.html` pod adresem pierwotnym zawsze zawiera wersję ujednoliconą (na KK tak wygląda); jeśli nie — pobierać tekst wskazany w `references["Tekst jednolity"]`.

---

## Idempotencja i wznawialność ingestu (wymóg: re-run bez ponownego przetwarzania)

Rejestr stanu w tabeli `documents` — maszyna stanów per dokument:

```
discovered → fetched → normalized → chunked → embedded → indexed
                                  ↘ failed (reason, attempt_count)
```

Zasady (każda objęta testem — patrz T-IDEM):
1. **Klucz naturalny** `(source, external_id)` UNIQUE — np. `(SAOS, 227221)`, `(ELI, DU/1997/553)`. Wszystkie zapisy to upsert po tym kluczu → re-run nigdy nie duplikuje.
2. **Pomijanie po `content_hash`:** dokument z identycznym hashem i statusem `indexed` jest pomijany **bez żadnej pracy** (bez normalizacji, bez embeddingu). To jest główny mechanizm „nie przerabiaj ponownie".
3. **Zmiana treści:** inny hash → pełne re-procesowanie dokumentu; nowe chunki **zastępują** stare w jednej transakcji (delete+insert) — zero osieroconych chunków.
4. **Checkpoint synchronizacji:** tabela `sync_state(source, last_modification_date, last_run_at)`; checkpoint przesuwany **po każdej stronie wyników** (nie po całym przebiegu) → przerwanie (Ctrl+C, crash, deploy) nie traci postępu; restart kontynuuje od checkpointu.
5. **Embedding warunkowy:** chunk ma `embedded_with` (model+wersja); embedujemy tylko chunki bez embeddingu lub z innym modelem. Test ingestu na tej samej próbce = **0 wywołań** `IEmbeddingProvider`.
6. **Kwarantanna błędów:** dokument nieparsowalny → `failed` + powód + licznik prób; nie blokuje reszty przebiegu; retry w kolejnych runach do limitu (np. 3), potem wymaga ręcznej decyzji.

---

## Scenariusze testowe (do zaimplementowania w `tests/`)

Fixtures: zapisane **realne odpowiedzi API** (JSON orzeczeń SAOS — w tym przypadki z błędnymi datami; fragment `text.html` KK z art. 148) w `tests/fixtures/`. Testy integracyjne na Postgres przez Testcontainers (lub compose testowy).

**T-NORM — normalizacja SAOS (unit):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | HTML→tekst na realnym orzeczeniu | tagi usunięte, akapity `\n\n`, komentarze usunięte, `anon-block` zachowany jako tekst |
| 2 | `judgmentDate` = „3013-12-04" | dokument przyjęty; data skorygowana z treści lub flaga `quality_issue`; brak wyjątku |
| 3 | ekstrakcja metadanych | sygnatura, sąd, wydział poprawne z `courtCases`/`division` |
| 4 | `referencedRegulations` | sparsowane do (rok, nr, pozycja, [artykuły]) z pola `text` |
| 5 | puste `summary`/`decision`/`legalBases` | normalizacja przechodzi, pola opcjonalne |
| 6 | detekcja sekcji | „UZASADNIENIE" otwiera sekcję `justification`; sentencja i komparycja rozpoznane |

**T-ACT — normalizacja aktów ELI (unit):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | parsowanie art. 148 KK z fixture | artykuł z 4 paragrafami; `article=148`; tekst „Kto zabija człowieka…" obecny |
| 2 | nagłówek kontekstowy chunka | zawiera tytuł aktu + rozdział + numer artykułu |
| 3 | artykuł dłuższy niż limit tokenów | podział po paragrafach, nagłówek powtórzony w każdym chunku |
| 4 | lokalizator | `eli_id`, kotwica HTML i `displayAddress` poprawne |

**T-CHUNK — chunking (unit):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | długie uzasadnienie | każdy chunk ≤ 512 tokenów wg tokenizera TEI |
| 2 | sąsiednie chunki | overlap obecny |
| 3 | mapowanie | `substring(original, char_start, char_end)` == tekst chunka |
| 4 | krótki dokument / pusty tekst | 1 chunk / 0 chunków + warning, bez wyjątku |

**T-IDEM — idempotencja ingestu (integracyjne; KLUCZOWE dla wymogu re-run):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | dwukrotny ingest tej samej próbki | identyczna liczba dokumentów/chunków; **0 wywołań embeddingu** w 2. przebiegu (weryfikacja na mocku `IEmbeddingProvider`) |
| 2 | zmiana treści 1 dokumentu (nowy hash) | tylko ten dokument re-procesowany; stare chunki usunięte, brak osieroconych |
| 3 | przerwanie przebiegu po N dokumentach (symulowany crash) | restart dochodzi do stanu identycznego jak przebieg bez przerwania; nic nie liczone podwójnie |
| 4 | checkpoint per strona | ponowny run z checkpointem nie pobiera ponownie przetworzonych stron (weryfikacja liczby wywołań HTTP na mocku konektora) |
| 5 | dokument nieparsowalny | status `failed` + powód; reszta przebiegu ukończona; retry w następnym runie |
| 6 | zmiana wersji modelu embeddingu w konfiguracji | chunki z `embedded_with` ≠ konfiguracja wykryte jako wymagające re-embeddingu (i tylko one) |

**T-RETR — retrieval hybrydowy (integracyjne, mały korpus testowy ~20 dok.):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | „art. 148 kodeksu karnego" | chunk art. 148 KK w top-3 (ścieżka BM25) |
| 2 | „jaka kara grozi za zabójstwo" | art. 148 w top-K (ścieżka dense) |
| 3 | sygnatura „II K 84/16" | orzeczenie w top-1 |
| 4 | filtr `courtType=COMMON` | wyniki SN wykluczone |
| 5 | RRF | dokument wysoko w obu rankingach > dokument wysoko tylko w jednym |
| 6 | `TeiEmbeddingProvider` (unit, mock HTTP) | prefiks `zapytanie: ` dodany do zapytania, NIE do pasaży |

**T-ABST — bramka abstynencji:**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | pytanie spoza korpusu (np. prawo lotnicze przy korpusie karnym) | odpowiedź abstynencyjna; **0 wywołań LLM** |
| 2 | pytanie w korpusie | przechodzi bramkę, generowanie uruchomione |
| 3 | podniesiony próg | więcej odmów na tym samym zestawie (próg faktycznie steruje) |

**T-FABR — anty-fabrykacja cytatów (unit, LLM zmockowany):**
| # | Scenariusz | Oczekiwanie |
|---|---|---|
| 1 | odpowiedź cytuje źródło [n] obecne w kontekście | przechodzi |
| 2 | mock LLM zwraca cytat spoza kontekstu | wykryty, oflagowany/usunięty |
| 3 | odpowiedź zawiera sygnaturę nieobecną w kontekście | wykryta |

**T-E2E — golden set (E5):** 50–100 pytań wg zadań 5.1–5.3 (w korpusie / poza / podchwytliwe, w tym pytanie o **nieistniejący paragraf** — oczekiwana abstynencja, nie konfabulacja).

---

## Schemat metadanych (wymaganie d) — projektować od dnia 1

Metadane są **load-bearing** dla cytatów i abstynencji. Chunk musi znać swój dokument-rodzica i dokładny lokalizator źródła.

**Wspólne:** `document_id`, `source` (SAOS/ISAP/EURLEX), `doc_type` (orzeczenie/akt/artykuł/książka), `title`, `source_url`, `source_modification_date` (sync przyrostowy), `content_hash` (dedup/wykrywanie zmian), `ingested_at`, **`embedding_model` + wersja** (model zablokowany na życie korpusu; nigdy nie mieszać modeli w indeksie).

**Akty prawne:** `eli_id`, `act_type` (ustawa/rozporządzenie/…), `publisher` (DU/MP), `year`, `position`, `article`/`paragraph` (lokalizator), `in_force_from`/`in_force_to`.

**Orzeczenia:** `court`, `court_type`, `sygnatura_akt`, `judgment_date`, `judges`, `legal_bases`. **Uwaga licencyjna:** wzbogacenia SAOS (tezy, słowa kluczowe, streszczenia) mogą być chronione osobno od treści orzeczenia — przechowywać treść orzeczenia, wzbogacenia traktować ostrożnie (patrz sekcja prawna).

**Chunk → rodzic:** `chunk_id`, `parent_document_id`, `chunk_index`, `char_start`/`char_end`, `token_count`. Retrieval po chunkach, ale **cytujemy i pokazujemy cały dokument-rodzic** (np. całe orzeczenie / pełny artykuł). Retrofit tej relacji jest bolesny — robimy od początku.

---

## Pipeline RAG + ugruntowanie (rdzeń wartości)

1. **Zapytanie** użytkownika → prefiks `zapytanie: ` → embedding (TEI) + zapytanie BM25.
2. **Retrieval hybrydowy** w pgvector: top-N dense + top-N BM25 → **fuzja RRF** → kandydaci (+ filtrowanie po metadanych, np. tylko obowiązujące akty / typ sądu).
3. *(roadmap)* **Reranker** top-N → top-K.
4. **Bramka abstynencji:** jeśli najwyższy score < próg (lub reranker niepewny) → odpowiedź „Nie mam wystarczających źródeł, by odpowiedzieć — zawęź pytanie lub wskaż akt". **Nie generujemy.**
5. **Generowanie** (cloud LLM) z twardym system promptem: *odpowiadaj wyłącznie z dostarczonego kontekstu; każdą tezę poprzyj cytatem z lokalizatorem (akt + art./§ + ELI, albo sąd + sygnatura + data); jeśli kontekst nie wystarcza — powiedz to.*
6. **Weryfikacja po generacji (anty-fabrykacja cytatów):** sprawdź, że zacytowane źródła faktycznie są w kontekście; odrzuć/oflaguj wymyślone.
7. **UI:** odpowiedź + lista źródeł ze snippetami i linkami → prawnik weryfikuje, nie ufa na słowo.

---

## Bramka prawna i RODO (do rozstrzygnięcia PRZED ingestią/serwowaniem — nie blokuje pisania kodu)

**Licencje danych (produkt prawniczy — to istotne):**
- Polskie prawo (**Ustawa o prawie autorskim, art. 4**) wyłącza spod ochrony *akty normatywne* oraz *urzędowe dokumenty i materiały* → teksty ustaw (ISAP/ELI) i orzeczeń (SAOS) jako takie są poza prawem autorskim. **Do potwierdzenia.**
- Ryzyka rezydualne: (a) **prawo sui generis do bazy danych** — masowa ekstrakcja całej bazy może naruszać, nawet gdy pojedyncze rekordy nie są chronione → unikać hurtowego pobierania całości, działać przyrostowo/celowo; (b) **wzbogacenia redakcyjne SAOS** (tezy, słowa kluczowe) mogą być chronione osobno → nie reużywać.
- EUR-Lex i ELI/Sejm: licencje czyste (CC-BY/CC0). Zweryfikować ToS SAOS.

**RODO / przepływ danych (cloud LLM w MVP):**
- Zadeklarować postawę: co jest logowane, co opuszcza VPS, retencja. W MVP **nie wymagać danych osobowych klientów** do korzystania (pytania o stan prawny, nie o konkretne sprawy z PII).
- To uzasadnia istnienie późniejszej anonimizacji i pakietu Diamond (Bielik lokalnie = nic nie wychodzi).

---

## Dekompozycja: epiki → zadania (refinement)

Sizing: **S** = 1–2 wieczory, **M** = ~tydzień po godzinach, **L** = 2–3 tygodnie po godzinach. Kolejność epików = kolejność realizacji; zależności wskazane przy zadaniach.

### E0 — Fundament i decyzje (bramka startowa) — *odblokowuje wszystko*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 0.1 | Repo + solucja .NET + szkielet projektów (Domain/Ingestion/Embeddings/Llm/Storage/Api/Web) | S | `dotnet build` przechodzi; struktura jak w sekcji Architektura |
| 0.2 | `infra/compose.yaml`: Postgres+pgvector + TEI (mmlw); maszyna Podman ≥8 GB | S | `podman compose up` → healthcheck TEI zwraca embedding; `CREATE EXTENSION vector` działa |
| 0.3 | Schemat bazy + migracje (dokumenty, chunki, wektory, metadane wg sekcji „Schemat metadanych") | M | Migracja stawia schemat; relacja chunk→rodzic; pola `content_hash`, `embedding_model` |
| 0.4 | Kontrakty domenowe: `ISourceConnector`, `IDocumentNormalizer`, `IEmbeddingProvider`, `ILlmProvider` + modele | S | Kompilujące się interfejsy + modele dokumentu/chunku; rejestracja w DI |
| 0.5 | **Bramka licencyjna**: potwierdzić art. 4 pr. aut., ToS SAOS, ryzyko sui generis; spisać wnioski | S | Notatka z decyzją „wolno ingestować i serwować" (lub ograniczenia) |
| 0.6 | Wybór wycinka korpusu MVP (dziedzina prawa / zakres lat / sądy) | S | Zdefiniowany filtr na API SAOS + szacowany wolumen (dok/chunki) |

**Jak implementować (E0):**
- **0.2:** obrazy `docker.io/pgvector/pgvector:pg17` i `ghcr.io/huggingface/text-embeddings-inference:cpu-latest` z `--model-id sdadas/mmlw-retrieval-roberta-base` (zmienna env, podmienialna na large-v2 po locku); wolumeny na dane PG i cache modelu; healthchecki w compose. Najpierw podbić maszynę Podman do ≥8 GB (`podman machine set --memory 8192`), bo 2 GB nie udźwignie TEI+PG.
- **0.3:** EF Core + `Pgvector.EntityFrameworkCore`; tabele: `documents` (klucz naturalny `(source, external_id)` UNIQUE, `status` maszyny stanów, `content_hash`, `quality_issues` jsonb, metadane wspólne + jsonb na specyficzne per typ), `chunks` (FK do dokumentu, `chunk_index`, `char_start/end`, `section`, `embedded_with`, kolumna `vector(768|1024)`, kolumna `tsvector` generowana z konfiguracją `'polish'` — wymaga sprawdzenia dostępności słownika polskiego w obrazie PG, fallback: `'simple'` + unaccent), `sync_state`, `conversations`/`messages` (E4). Indeksy: HNSW (lub IVFFlat przy małym RAM) na vector, GIN na tsvector.
- **0.4:** kontrakty jak w sekcji Architektura; modele rekordowe: `RawDocument` (źródłowy JSON + treść), `NormalizedDocument` (tekst, metadane, sekcje), `DocumentChunk`. DI przez `IServiceCollection`, konektory rejestrowane po kluczu źródła.
- **0.6 (ZMIERZONE 2026-06-11):** SUPREME odpada — zamrożony na 2016 (0 rekordów od 2019). **Rekomendowany wycinek: `courtType=COMMON&ccCourtType=APPEAL&judgmentDateFrom=2023-01-01`** — sądy apelacyjne (wyższa waga jurydyczna niż rejonowe), aktualne (najnowsze 2026-06-08), rząd ~8–10k dokumentów ≈ ~80–100k chunków (tani batch GPU, wykonalny nawet na CPU). Filtr daty zawsze z `judgmentDateTo` (np. dziś), bo w bazie są daty-śmieci w przyszłości (np. „3013-12-04") psujące sortowanie/zakresy. Pełne COMMON: APPEAL 16k / REGIONAL 53k / DISTRICT 32k (2021+) — dociągamy po walidacji.

### E1 — Ingestia SAOS (orzeczenia) — *zależy od E0*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 1.1 | `SaosConnector`: search/dump API, paginacja, `sinceModificationDate`, retry/rate-limit | M | Pobiera wycinek z 0.6; wznawia po przerwaniu; nie duplikuje |
| 1.2 | Normalizer orzeczeń: HTML→czysty tekst + ekstrakcja metadanych (sygnatura, sąd, data, podstawy prawne) | M | Na próbce 50 orzeczeń: tekst bez artefaktów HTML, metadane kompletne |
| 1.3 | Chunker: ~400–500 tokenów, overlap, respektuje granice sekcji orzeczenia | S | Chunki ≤ limitu modelu; `char_start/end` wskazują na oryginał |
| 1.4 | `TeiEmbeddingProvider` (batch + prefiks `zapytanie: ` tylko dla zapytań) | S | Embedding próbki zgodny wymiarowo; batch działa |
| 1.5 | Upsert do pgvector + dedup po `content_hash` | S | Ponowny przebieg = 0 nowych embeddingów |
| 1.6 | ✅ **A/B base vs large-v2 → lock modelu: `large-v2`** (PIRB NDCG@10 60.71 vs 56.38) | M | Decyzja zapisana w SESJA.md; `EmbeddedWith` w metadanych chunków |
| 1.7 | Masowy embedding wycinka na wynajętym GPU (RunPod/Vast.ai) + załadunek wektorów | M | Wycinek w pgvector; koszt i czas zmierzone (godziny, nie dni) |
| 1.8 | Worker przyrostowy (`BackgroundService`, sync dzienny) | S | Nowe orzeczenia z ostatnich dni pojawiają się w bazie automatycznie |

**Jak implementować (E1):**
- **1.1 (ZWERYFIKOWANE API):** dump API NIE ma filtra `courtType` (tylko daty + `sinceModificationDate`); search API ma filtry sądu, ale NIE ma `sinceModificationDate`. Stąd dwie ścieżki w `SaosConnector`: (a) **wczytanie początkowe wycinka** = search API (`courtType`+`ccCourtType`+`judgmentDateFrom/To`) → ID → pełny dokument przez `/api/judgments/{id}` (`.data`, HTML); (b) **sync przyrostowy** = dump API (`sinceModificationDate`) + filtr wycinka po stronie klienta (courtType + nazwa sądu „Sąd Apelacyjny"). Brak per-item daty modyfikacji → checkpoint w `sync_state` przez (watermark = czas startu runu) + numer strony do wznowienia; nadmiarowość pokrywa dedup po `content_hash`. `HttpClient` z Polly (retry/backoff, 429).
- **1.2:** `JudgmentNormalizer` wg specyfikacji w sekcji „Zweryfikowana struktura źródeł" (HtmlAgilityPack, walidacja dat, sekcje, `referencedRegulations`). Testy T-NORM na fixtures.
- **1.3:** `TokenAwareChunker` — liczenie tokenów przez TEI `/tokenize` (batch); parametry (cel/overlap/limit) w konfiguracji. Testy T-CHUNK.
- **1.4:** `TeiEmbeddingProvider`: `POST /embed` z batchem pasaży (bez prefiksu) i osobno zapytań (z `zapytanie: `); normalizacja wektorów wg wymagań modelu (sprawdzić na karcie HF, czy cosine wymaga L2-normalizacji).
- **1.5:** upsert w transakcji wg sekcji „Idempotencja"; testy T-IDEM (najważniejsze w epiku).
- **1.6:** skrypt A/B: ta sama próbka (~200 dok.) embedowana oboma modelami do dwóch kolekcji; ≥20 pytań; porównanie recall@10 ręcznie ocenionych trafień; decyzja zapisana w planie/README.
- **1.7:** ten sam worker w trybie `--bulk` odpalony na boxie GPU (compose z obrazem TEI GPU `…:latest` zamiast `cpu-latest`); wynik: eksport wektorów (COPY) lub bezpośredni zapis do PG przez tunel; zmierzyć chunk/s i koszt.
- **1.8:** `BackgroundService` z harmonogramem (np. co 24 h): dump API od checkpointu → pipeline → checkpoint. Logowanie liczby nowych/zmienionych/pominiętych.

### E2 — Ingestia ELI/ISAP (akty prawne) — *zależy od E0; równolegle z E1 po 1.4*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 2.1 | `EliSejmConnector`: api.sejm.gov.pl/eli, pobieranie tekstów aktów (DU/MP) | M | Pobiera wskazane ustawy (np. KC, KPC, KK) z identyfikatorami ELI |
| 2.2 | Normalizer aktów: **podział po artykułach/paragrafach** (struktura aktu = naturalny chunk + lokalizator cytatu) | M | Chunk = artykuł (lub grupa); metadane `article`, `eli_id` poprawne |
| 2.3 | Metadane obowiązywania (`in_force_from/to`) + filtr „tylko obowiązujące" | S | Zapytanie potrafi wykluczyć akty uchylone |

**Jak implementować (E2):**
- **2.1:** `EliSejmConnector`: metadane z `GET /eli/acts/DU/{year}/{pos}`, treść z `…/text.html`; `changeDate` → `source_modification_date` (sync przyrostowy identyczny mechanizm jak SAOS). Start od listy kluczowych kodeksów (KC DU/1964/93, KPC DU/1964/296, KK DU/1997/553, KPK DU/1997/555, KP DU/1974/141 — pozycje zweryfikować), potem roczniki.
- **2.2:** `ActNormalizer` wg zweryfikowanej struktury: parsowanie `div.unit_arti`/`unit_para` (deterministyczne), chunk = artykuł z nagłówkiem kontekstowym, lokalizator = `eli_id`+`article`+kotwica. Testy T-ACT. Sprawdzić na 1. akcie, czy `text.html` = tekst ujednolicony.
- **2.3:** `inForce` z metadanych ELI; filtr w retrieval (`WHERE doc_type != 'act' OR in_force`).

### E3 — Retrieval + API (rdzeń wartości) — *zależy od E1 (min. próbka w bazie)*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 3.1 | Wyszukiwanie hybrydowe: dense (pgvector) + BM25 (`tsvector` polski) + fuzja RRF | M | Zapytanie o numer artykułu/sygnaturę trafia (BM25); semantyczne trafia (dense) |
| 3.2 | Filtrowanie po metadanych (typ sądu, zakres dat, tylko obowiązujące) | S | Filtry działają w zapytaniu SQL |
| 3.3 | **Bramka abstynencji**: próg score → „nie mam wystarczających źródeł" | M | Pytanie spoza korpusu NIE generuje odpowiedzi; próg konfigurowalny |
| 3.4 | `ILlmProvider` (Claude/OpenAI) + system prompt ugruntowania (cytuj lokalizatory, nie wychodź poza kontekst) | M | Odpowiedzi zawierają cytaty z lokalizatorami; streaming działa |
| 3.5 | Endpoint chat (SSE) + retrieval endpoint | M | Frontend może streamować odpowiedź + dostaje listę źródeł |
| 3.6 | **Anty-fabrykacja**: walidacja, że cytaty z odpowiedzi istnieją w dostarczonym kontekście | M | Wymyślony cytat jest wykrywany i flagowany/usuwany |
| 3.7 | Auth minimalne (konta + klucz/sesja; bez płatności na MVP) | S | Dostęp tylko dla zaproszonych testerów |

**Jak implementować (E3):**
- **3.1:** jedno zapytanie SQL z dwoma CTE: dense (`ORDER BY embedding <=> @query LIMIT 50`) + BM25 (`ts_rank` na tsvector, konfiguracja polska/simple+unaccent) → fuzja RRF w SQL (`1/(60+rank)`) lub w C#. Testy T-RETR.
- **3.3:** bramka na podstawie similarity top-1 i/lub liczby kandydatów powyżej progu; próg w konfiguracji; zwracać też „powód odmowy" do UI. Testy T-ABST. Kalibracja w 5.3.
- **3.4:** `ILlmProvider` z metodą `StreamCompletionAsync(systemPrompt, messages, contextDocs)`; implementacje Anthropic/OpenAI (oficjalne SDK .NET). Kontekst budowany z chunków + **pełniejszy fragment rodzica** (sekcja, w której jest chunk); każdy doc z numerem `[1]..[K]` i lokalizatorem. System prompt: odpowiadaj WYŁĄCZNIE z kontekstu; każda teza z cytatem `[n]`; brak pokrycia → powiedz wprost.
- **3.5:** ASP.NET Core minimal API; `POST /api/chat` zwraca SSE: event `sources` (lista źródeł z lokalizatorami) → eventy `token` → event `done` (z wynikiem walidacji cytatów).
- **3.6:** parser cytatów `[n]` z odpowiedzi + walidacja: n ∈ dostarczony kontekst; dodatkowo regex na sygnatury/„art. X" — jeśli wskazują poza kontekst → flaga w event `done`. Testy T-FABR.
- **3.7:** ASP.NET Identity lub prostsze: tabela users + cookie auth + rejestracja na zaproszenie (kod zaproszenia).

### E4 — UI (chat) — *zależy od E3.5*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 4.1 | Chat w stylu ChatGPT (Blazor, streaming) | M | Rozmowa płynnie streamuje |
| 4.2 | **Panel źródeł**: snippety + lokalizatory + linki do SAOS/ELI | M | Każda odpowiedź pokazuje klikalne źródła; link otwiera realny dokument |
| 4.3 | Historia rozmów (zapis w Postgres) | S | Użytkownik wraca do wcześniejszych rozmów |
| 4.4 | Komunikat abstynencji jako wyraźny stan UI (nie „błąd") | S | „Brak wystarczających źródeł" + sugestia zawężenia |

### E5 — Ewaluacja i jakość — *równolegle z E3/E4, kończy MVP*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 5.1 | **Golden set**: 50–100 pytań (w korpusie / poza korpusem / podchwytliwe — np. nieistniejący paragraf) | M | Zestaw z oczekiwanymi źródłami i oczekiwaną abstynencją |
| 5.2 | Harness ewaluacyjny: trafność cytatów, poprawność abstynencji, porównanie vs „goły" Claude | M | Raport: % poprawnych cytatów, % poprawnej abstynencji, % halucynacji |
| 5.3 | Kalibracja progu abstynencji na golden secie | S | Próg zbalansowany (mało fałszywych odmów i fałszywych odpowiedzi) |
| 5.4 | Reranker (polish-reranker / bge-reranker-v2-m3) — *jeśli 5.2 pokaże potrzebę* | M | Mierzalny wzrost precyzji@K na golden secie |

### E6 — Deploy i ops (MVP live) — *zależy od E3/E4*
| # | Zadanie | Sizing | Kryterium akceptacji |
|---|---|---|---|
| 6.1 | VPS Hetzner 8 GB + deploy compose + domena + TLS | S | Aplikacja dostępna publicznie po HTTPS |
| 6.2 | Backup Postgres (dump + offsite) | S | Odtworzenie bazy z backupu przetestowane |
| 6.3 | Minimalny monitoring: logi, alert gdy worker/TEI padnie, licznik kosztów LLM API | S | Wiesz, gdy coś leży i ile wydajesz |

### Po MVP (kolejność wg wartości)
- **E7 — rozszerzenia źródeł:** EUR-Lex (CC-BY/CC0), TK przez SAOS, kolejne typy dokumentów (artykuły, książki .md) — *dowód rozszerzalności: nowy `ISourceConnector`/`IDocumentNormalizer`*.
- **E8 — desktop:** PDF→MD (Docling, MIT) + lokalna anonimizacja PII (Presidio + polski NER); Python jako osobne sidecary.
- **E9 — pakiet Diamond:** lokalny Bielik 11B na Mac mini u klienta (Ollama/llama.cpp, GGUF); ten sam kod dzięki `ILlmProvider`.

### Definicja ukończenia MVP
Zaproszony prawnik-tester zadaje pytanie z zakresu wycinka korpusu → dostaje odpowiedź z poprawnymi, klikalnymi cytatami (ELI/sygnatura); pytanie spoza zakresu → jawna abstynencja. Golden set: wyraźnie mniej halucynacji niż goły LLM. Koszt utrzymania ≤ ~15 €/mc + zużycie LLM API.

---

## Następne kroki (od zaraz, w kolejności)

1. **0.5 Bramka licencyjna** — przeczytać ToS SAOS i regulamin api.sejm.gov.pl, spisać wnioski (1 wieczór; ryzyko prawne rozstrzygnąć zanim kod urośnie).
2. **0.6 Wybór wycinka korpusu** — zdecydować dziedzinę/zakres (sugestia: orzeczenia SN + sądów apelacyjnych z ostatnich ~5 lat w jednej dziedzinie, np. prawo pracy lub cywilne — częste pytania, mierzalna jakość) + 2–3 kodeksy z ISAP.
3. **0.1–0.4 Fundament** — repo, compose, schemat, kontrakty (≈1 tydzień wieczorami).
4. **Spike 1.1+1.2** — `SaosConnector` na 50 orzeczeniach end-to-end do bazy (walidacja formatu danych SAOS zanim zbudujemy resztę).
5. **1.6 A/B embeddingów** → lock modelu → **1.7 batch GPU**.
6. Dalej wg epików E3 → E4 → E5 → E6.

---

## Weryfikacja (jak przetestować end-to-end)

1. `podman compose up` w `infra/` → Postgres+pgvector + TEI wstają; healthcheck TEI (embedding testowego zdania) i `vector` extension w Postgres. (Wcześniej: maszyna Podman ≥8 GB.)
2. Uruchom worker ingestii na **małej próbce**: ~kilkadziesiąt orzeczeń z SAOS + kilka aktów z ELI. Sprawdź w bazie: chunki mają `parent_document_id`, lokalizatory (sygnatura / ELI + artykuł), `content_hash`, `embedding_model`.
2a. **Pomiar przepustowości:** zmierz chunk/s w trybie batch (TEI) na próbce i ekstrapoluj na wycinek MVP; potwierdź, że masowy embedding na GPU mieści się w godzinach, nie dniach. Test dedupu: ponowny przebieg nie re-embeduje (po `content_hash`).
3. Test sync przyrostowego: ponowny przebieg nie duplikuje (po `content_hash`), pobiera tylko zmienione (po `source_modification_date`).
4. Zapytanie przez API o temat **z** korpusu → odpowiedź zawiera trafne cytaty; kliknięcie źródła otwiera realny akt/orzeczenie (ELI/sygnatura się zgadza).
5. **Test abstynencji:** zapytanie **spoza** korpusu → odpowiedź „nie mam wystarczających źródeł", brak zmyślonego paragrafu.
6. **Test anty-fabrykacji:** zweryfikuj, że żaden cytat w odpowiedzi nie wskazuje na nieistniejące w kontekście źródło.
7. **Porównanie jakości:** ten sam zestaw pytań prawnych przez nasz RAG vs Claude bezpośrednio — ocena trafności i bezpieczeństwa (czy zmyśla).

---

## Kluczowe pliki do utworzenia (greenfield)

- `infra/compose.yaml` — Postgres+pgvector, TEI (mmlw), api, worker (Podman).
- `src/PrawoRAG.Domain/` — `ISourceConnector`, `IDocumentNormalizer`, modele dokumentu/chunku, schemat metadanych.
- `src/PrawoRAG.Ingestion/` — `BackgroundService`, `SaosConnector`, `EliSejmConnector`, chunker.
- `src/PrawoRAG.Embeddings/` — `IEmbeddingProvider` + `TeiEmbeddingProvider` (z prefiksem `zapytanie: ` dla zapytań).
- `src/PrawoRAG.Storage/` — repozytorium pgvector (Npgsql + Pgvector), hybryda dense+BM25+RRF.
- `src/PrawoRAG.Llm/` — `ILlmProvider` + implementacja Claude/OpenAI; system prompt ugruntowania.
- `src/PrawoRAG.Api/` — endpoint chat (SSE), retrieval, bramka abstynencji, weryfikacja cytatów.
- `src/PrawoRAG.Web/` — chat UI + panel źródeł.
