# Wznowienie pracy (handoff)

Punkt powrotu do projektu **PrawoRAG**. Pełny kontekst: [PLAN.md](PLAN.md),
decyzje i odkrycia: [USTALENIA-I-ODKRYCIA.md](USTALENIA-I-ODKRYCIA.md).

## Gdzie jesteśmy
MVP zbudowane i przetestowane w warstwie ingestii i retrievalu/czatu:
- ✅ **E0** fundament · ✅ **E1** ingestia SAOS (idempotentna) · ✅ **E3** retrieval hybrydowy + czat SSE z ugruntowaniem, abstynencją, anty-fabrykacją.
- ✅ **Ingestia dwufazowa** (2026-07): `fetch` (SAOS→magazyn surowych na dysku) rozdzielone od `process`
  (magazyn→normalize/chunk/embed→baza). Re-processing po zmianie kodu **bez ponownego pobierania**.
  Tryby `Ingestion:Mode` = fetch | process | fetch-process (domyślny) | stream (stara ścieżka).
- ✅ **Lokalny LLM** (pakiet Diamond): `OpenAiCompatibleLlmProvider` (Ollama/llama.cpp), przełącznik
  `Llm:Provider` = claude | local. Bielik konfigurowalny w `Llm:Local`.
- ✅ **Fix timeoutu SAOS**: search z filtrami liczy ~8–15s po stronie serwera; `Saos:AttemptTimeoutSeconds`
  (domyślnie 45s) zamiast domyślnych 10s resilience handlera.
- **34 testy zielone** (T-NORM, T-CHUNK, T-IDEM na żywym PG, T-ABST, T-FABR + round-trip magazynu,
  fetch/process, SSE lokalnego LLM).
- Smoke end-to-end: realne orzeczenia apelacyjne przeszły cały pipeline; `/api/search` i `/api/chat` działają.
- Git: gałąź `main` wypchnięta na **`daxter44/LegalAi`** (prywatne konto). Weryfikacja lokalna na M4:
  [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md).

## Jak wznowić pracę (ta maszyna lub M4)
```bash
# 1. Infrastruktura
cd infra && podman compose up -d                 # Postgres+pgvector + TEI (mmlw); poczekaj na "Ready" w logach TEI
#    (maszyna Podman musi mieć ≥8 GB; na WSL zegar VM bywa rozjechany — patrz USTALENIA)

# 2. Schemat (idempotentne; jeśli baza świeża)
dotnet tool restore && dotnet ef database update --project src/PrawoRAG.Storage

# 3. Testy — szybka weryfikacja, że wszystko działa
dotnet test

# 4. Ingestia próbki — dwufazowo (fetch+process domyślnie); process powtarzalny bez pobierania
Ingestion__MaxItems=50 dotnet run --project src/PrawoRAG.Ingestion
#   albo osobno: Ingestion__Mode=fetch ...  potem  Ingestion__Mode=process ...

# 5. API (czat wymaga klucza)
ANTHROPIC_API_KEY=sk-... dotnet run --project src/PrawoRAG.Api
```
Stan bazy zależy od tego, czy kontenery wciąż żyją (`podman ps`). Dane w wolumenach `praworag_pgdata` / `praworag_tei-cache` przetrwają restart kontenerów.

## Otwarte decyzje (czekają na Ciebie)
1. ✅ **Push na GitHub** — zrobione: `daxter44/LegalAi` (prywatne). Następne: weryfikacja jakości na M4 (poziom 3, Bielik lokalnie) — [URUCHOMIENIE-M4.md](URUCHOMIENIE-M4.md).
2. **1.6 — model embeddingów:** A/B `mmlw-base` (768) vs `large-v2` (1024) → **lock przed masową ingestią** (zmiana = re-embedding całości).
3. **0.5 — bramka licencyjna:** ToS SAOS + regulamin api.sejm.gov.pl + art. 4 pr. aut. (przed pełną ingestią/serwowaniem).
4. **Próg abstynencji wymaga kalibracji** — surowy cosine mmlw słabo rozdziela „wiem"/„nie wiem" (patrz USTALENIA). Potrzebny golden set + reranker.

## Następne kroki (priorytety)
- **E2 — akty ELI/ISAP** (KC/KK…): twarde sygnały do retrievalu (art. 148 KK) i lepszy materiał do testów niż same sprawy cywilne.
- **Bulk embedding na M4** dla większego korpusu: na start wystarczy nasz stack .NET+TEI na natywnym M4 (CPU, ~min dla setek dok.); skrypt Python+MPS (`tools/bulk-embed/`) dopiero przy dużym korpusie (8–10 tys.+).
- **E5 — golden set + kalibracja progu + reranker** (żeby abstynencja realnie odróżniała brak pokrycia).
- **E4 — UI (Blazor)**: czat w stylu ChatGPT + panel źródeł.
- **1.7 — pełny korpus** (apelacyjne 2023+ ≈ 8–10 tys.) na M4/GPU, gdy jakość potwierdzona.

## Szybka orientacja w kodzie
- Kontrakty: `src/PrawoRAG.Domain` (`ISourceConnector`, `IDocumentNormalizer`, `IChunker`, `IEmbeddingProvider`, `ILlmProvider`, `IRetriever`).
- Ingestia: `src/PrawoRAG.Ingestion` (`Saos/`, `Chunking/`, `IngestionPipeline`, `IngestionRunner`, `Program.cs`).
- Retrieval: `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs`.
- Ugruntowanie/LLM: `src/PrawoRAG.Llm` (`ClaudeLlmProvider`, `Grounding/`).
- API: `src/PrawoRAG.Api/Program.cs` (`/api/search`, `/api/chat`).
- Testy: `tests/PrawoRAG.Tests` (fixtures z realnych odpowiedzi SAOS w `Fixtures/Saos`).
