# Ustalenia i odkrycia (log decyzji)

Uzupełnienie do [PLAN.md](PLAN.md): kluczowe decyzje oraz fakty **zweryfikowane na żywo**
podczas budowy MVP (2026-06-11). Plan = zamierzenia; ten dokument = co potwierdziliśmy w praktyce.

## Decyzje produktowe / architektoniczne
- **LLM w MVP = cloud API (Claude/OpenAI)** za `ILlmProvider`. Bielik wraca w pakiecie **Diamond** —
  lokalnie u klienta na **Mac mini** (dane nie opuszczają kancelarii).
- **Hosting = tani VPS Hetzner ≥ 8 GB RAM** (Postgres + TEI + API). Domyślne 4 GB nie wystarczy.
- **Stack = .NET 10** (orkiestracja) — „powolność" wcześniejszych POC wynikała z inference modeli,
  nie z języka. Python tylko dla komponentów ML, które istnieją wyłącznie w nim (później, jako sidecar).
- **Baza = Postgres + pgvector** (jedna baza: wektory + BM25 + metadane + użytkownicy). Min. ops.
- **Embedding = mmlw-retrieval-roberta przez TEI**, schowany za `IEmbeddingProvider`.
  **Model zablokowany na życie korpusu** — zapytanie i baza muszą być tym samym modelem;
  zmiana = re-embedding całości. Wybór base(768) vs large-v2(1024) przez A/B PRZED masową ingestią (1.6 — otwarte).
- **Rozszerzalność:** nowe źródło = `ISourceConnector`; nowy typ dokumentu = `IDocumentNormalizer`.
- **Konteneryzacja = Podman** (nie Docker); `compose.yaml` neutralny.

## Źródła danych — zweryfikowane na żywych API
- **SAOS** (`www.saos.org.pl/api`): dump API ma `sinceModificationDate` ale **NIE** `courtType`;
  search API ma filtry sądu (`courtType`, `ccCourtType`, `judgmentDateFrom/To`) ale **NIE** `sinceModificationDate`.
  Stąd ingestia: initial = search→ID→`/judgments/{id}`; przyrost = dump+filtr po stronie klienta.
  Wymaga nagłówka **`Accept: application/json`** (inaczej 406).
- **Świeżość SAOS per typ sądu (krytyczne):** aktualizowane są tylko **sądy powszechne (COMMON)**
  (najnowsze 2026-06). SN (~2016), TK (~2015), KIO (~2018) — zamrożone; **ADMINISTRACYJNE = 0 rekordów**.
  ⇒ koryguje założenie, że TK/NSA „przez SAOS" dadzą bieżące orzecznictwo. Wycinek MVP = COMMON apelacyjne 2023+.
- **Jakość danych SAOS:** błędne daty (`3013-12-04`, `2101-...`), sklejone nazwiska sędziów,
  nadmiarowe `referencedRegulations`. Dane osobowe **zanonimizowane u źródła** (`<span class="anon-block">`).
  Normalizer waliduje daty (1990–dziś, fallback z treści) i NIE odrzuca dokumentu — flaguje `QualityIssues`.
- **ELI/Sejm** (`api.sejm.gov.pl/eli`): `text.html` aktu ma deterministyczną strukturę
  `div.unit_arti`/`unit_para` → chunk = artykuł. `changeDate` → sync przyrostowy. (E2 — do zrobienia.)

## Odkrycia techniczne (gotchas)
- **mmlw nie ma plików ONNX** → TEI przełącza się na backend **Candle + safetensors** (działa, 768 wymiarów).
- **TEI CPU: max batch = 32** → `IEmbeddingProvider` batchuje żądania (`TeiOptions.MaxBatch`).
- **EF Core 10.0.9 wymuszone jawnie** w Storage (Pgvector.EntityFrameworkCore 0.3.0 ciąga 10.0.4 → konflikt).
- **HttpClient:** BaseAddress musi mieć końcowy `/`, ścieżki względne bez wiodącego `/`.
- **Podman/WSL (maszyna dev):** zegar VM dryfuje (psuje `pull` błędem TLS) — sync z hosta przez ssh `date`;
  `--memory` nie utrwala się bez `wsl --shutdown`; realnie WSL2 alokuje pamięć dynamicznie (~15 GB dostępne).
- **Wydajność bulk embeddingu:** CPU (zwłaszcza w zdławionej VM WSL) jest wąskim gardłem (~1 chunk/s).
  Bulk należy robić **poza produkcją** na mocnej maszynie — **Apple Silicon M4** (natywnie, ew. MPS) lub wynajęty GPU.
- **Bramka abstynencji — wynik kalibracji:** surowy cosine mmlw jest **słabo rozdzielony** — zapytanie
  spoza korpusu dostaje similarity ~0.78, tyle co trafne. Próg 0.55 przepuszcza wszystko. ⇒ abstynencji
  NIE wdrażać na surowym cosine; wymaga **golden setu (5.1) + kalibracji (5.3) i prawdopodobnie rerankera (5.4)**.
  Bramka działa mechanicznie (testy T-ABST), ale próg jest do ustawienia na realnych danych.

## Status epików (2026-06-11)
- ✅ **E0** fundament (solucja, compose, schemat+migracja na żywym pgvector, kontrakty).
- ✅ **E1** ingestia SAOS (connector, normalizer, chunker, embedding, pipeline z idempotencją). Smoke E2E OK.
- ⬜ **E2** akty ELI/ISAP (da art. 148 KK itp. — twarde sygnały do retrievalu i testów).
- ✅ **E3** retrieval hybrydowy (dense+BM25+RRF) + API chat SSE + ugruntowanie + abstynencja + anty-fabrykacja.
- ⬜ **E4** UI (Blazor), **E5** golden set + kalibracja + reranker, **E6** deploy/ops.
- ⬜ **1.6** A/B base vs large → lock modelu · **1.7** bulk na M4/GPU · **0.5** bramka licencyjna.

**Testy:** 31 zielonych (T-NORM, T-CHUNK, T-IDEM na żywym PG, T-ABST, T-FABR).

## Iteracja 2026-07 — ingestia dwufazowa, lokalny LLM, fix timeoutu
- **Rozdział fetch/process (rozwiązuje „drobna zmiana = pobierz całość od nowa").** Dotąd pipeline był
  jednofazowy i w pamięci — surowy HTML z SAOS był wyrzucany po przetworzeniu (`DocumentEntity` bez kolumny
  raw). Teraz: `IRawDocumentStore` + `FileSystemRawDocumentStore` (pliki `data/raw/{źródło}/{id}.json`,
  zapis atomowy tmp+rename, sanityzacja ExternalId dla ELI). `RawFetchRunner` (idempotentny — pomija
  istniejące) i `RawProcessRunner` (OFFLINE). Tryby `Ingestion:Mode` = fetch|process|fetch-process|stream.
  **`IngestionPipeline` nietknięty** — round-trip `SourcePayload` (JsonElement) wierny (jak w SaosFixtures).
- **SAOS search jest wolny (~8–15s), nie proxy.** Zmierzone: dokładne zapytanie konektora (`pageSize=100`
  + COMMON/APPEAL + sort) liczy się ~13,7s po stronie SAOS (wariancja niezależna od pageSize: `20`→7,6s,
  `10`→11,7s). Domyślny `AddStandardResilienceHandler` ma **10s na próbę** → ubijał każdą próbę. Fix:
  `Saos:AttemptTimeoutSeconds` (domyślnie 45s), `HttpClient.Timeout=Infinite` (rządzi resilience handler).
  Pojedyncze `/judgments/{id}` są szybkie (~110ms) — wolny jest tylko search.
- **Re-chunk wymaga unieważnienia (bez pobierania).** `process` pomija dokument o niezmienionym
  `content_hash` (dedup). Żeby przeliczyć chunki po zmianie chunkera: wyczyść dokumenty źródła w bazie
  (lokalnie) i uruchom `process` ponownie — zero ruchu do SAOS.
- **Lokalny LLM (pakiet Diamond).** `OpenAiCompatibleLlmProvider` (streaming SSE OpenAI: `delta.content`,
  `[DONE]`) — działa z Ollama/llama.cpp/LM Studio. Przełącznik `Llm:Provider` = claude | local; Bielik w
  `Llm:Local` (domyślnie `speakleash/Bielik-11B-v3.0-DFlash`). Weryfikacja na M4: [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md).
- **Ryzyko na M4:** obraz TEI `cpu-latest` bywa amd64 → na Apple Silicon emulacja/nie wstaje; fallback =
  natywny embedding (do zrobienia, jeśli TEI padnie).
- **Testy:** 34 zielone (baseline 23 bez zmian + 5 round-trip magazynu + 4 fetch/process + 2 SSE lokalnego LLM).
- **Git:** wypchnięte na `daxter44/LegalAi` (prywatne; tożsamość repo per-repo `daxter44`, globalna `diag` nietknięta).
