# PrawoRAG

RAG dla polskich prawników z **ugruntowaniem w cytowanych źródłach** i **jawną abstynencją**
(„nie mam wystarczających źródeł" zamiast halucynacji). Źródła MVP: orzeczenia **SAOS** + akty **ISAP/ELI**.

Stack: **.NET 10** (orkiestracja) · **Postgres + pgvector** (wektory + BM25 + metadane) ·
**TEI** serwujący embedding **mmlw-retrieval-roberta** (PL) · **Claude/OpenAI** za `ILlmProvider`.

## Wymagania
- .NET SDK 10
- Podman (lub Docker) + `podman compose`. Maszyna z **≥ 8 GB RAM** (TEI + Postgres).
- (opcjonalnie) `ANTHROPIC_API_KEY` — do generowania odpowiedzi w czacie.

## Szybki start
```bash
# 1. Infrastruktura: Postgres+pgvector + TEI (mmlw)
cd infra && podman compose up -d            # poczekaj aż TEI pobierze model (logi: "Ready")

# 2. Schemat bazy
dotnet tool restore
dotnet ef database update --project src/PrawoRAG.Storage

# 3. Ingestia próbki (liczbę reguluje Ingestion:MaxItems w appsettings lub env)
Ingestion__MaxItems=50 dotnet run --project src/PrawoRAG.Ingestion

# 4. API (czat + wyszukiwanie)
ANTHROPIC_API_KEY=sk-... dotnet run --project src/PrawoRAG.Api
#   POST /api/search  {"query":"...", "topK":5}
#   POST /api/chat    {"question":"..."}   (SSE: sources → token → done/abstain/error)
```

## Testy
```bash
dotnet test            # T-NORM, T-CHUNK (unit) + T-IDEM (wymaga żywego Postgresa) + T-ABST, T-FABR
```

## Architektura (separacja)
- `PrawoRAG.Domain` — kontrakty rozszerzalności (`ISourceConnector`, `IDocumentNormalizer`,
  `IChunker`, `IEmbeddingProvider`, `ILlmProvider`, `IRetriever`) + modele + lokalizator cytatu.
- `PrawoRAG.Ingestion` — worker: fetch → normalize → chunk → embed → upsert (idempotentnie).
- `PrawoRAG.Embeddings` — klient TEI.
- `PrawoRAG.Storage` — pgvector (EF Core) + `HybridRetriever` (dense + BM25 + RRF).
- `PrawoRAG.Llm` — `ClaudeLlmProvider`, prompt ugruntowania, walidator cytatów (anty-fabrykacja).
- `PrawoRAG.Api` — `/api/chat` (SSE) + `/api/search`; bramka abstynencji.

Dodanie źródła = nowy `ISourceConnector`. Dodanie typu dokumentu = nowy `IDocumentNormalizer`.

## Embedding masowy (bulk) — uwaga wydajnościowa
Masowy embedding na CPU jest wolny. Rób go **poza produkcją** na mocnej maszynie:
- **Apple Silicon (M4)**: ten sam stack natywnie jest dużo szybszy niż w zdławionej VM.
  TEI na Macu chodzi po CPU (brak Metal); dla maks. prędkości (GPU/MPS) bulk można puścić
  mmlw przez Python/sentence-transformers (`device="mps"`) — jednorazowy skrypt batch.
- **Wynajęty GPU** (RunPod/Vast): obraz TEI GPU, embedding całego korpusu w minuty.

Model embeddingów jest **zablokowany na życie korpusu** (zapytanie i baza muszą być tym samym
modelem). Zmiana modelu = re-embedding całości.
