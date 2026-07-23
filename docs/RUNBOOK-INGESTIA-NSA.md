# RUNBOOK: ingestia orzeczeń NSA/WSA (tylko wyroki)

Metoda pobierania + embedowania sądownictwa administracyjnego. Backfill z datasetu **JuDDGES/pl-nsa**
(nie scraping CBOSA), **tylko WYROKI** (odsiew postanowień — patrz `ANALIZA-INGESTIA-CBOSA.md`).
Wpina się w istniejący dwufazowy pipeline (fetch → magazyn surowych → process), z RÓWN-1/2/3.

## Dlaczego tak

- **Źródło = JuDDGES/pl-nsa** (Hugging Face, CC BY 4.0): pełne teksty + metadane, zero obciążania
  serwera sądu, natychmiast dostępne. Własny crawler CBOSA na deltę/braki — osobny, późniejszy klocek.
- **Tylko wyroki** (`judgment_type LIKE 'Wyrok%'`): ~650 tys. zamiast 2,39 mln → ~8 mln chunków
  ≈ ~20 GB, ten sam rząd co obecny korpus (mieści się na 3060). Postanowienia = proceduralny szum.
- **Prawomocne i nieprawomocne** — obie (jak lexedit); nieprawomocny wyrok bywa samą sentencją,
  uzasadnienie dochodzi później (delta doładuje w przyszłości).

## Krok 1 — fetch do magazynu surowych (Python, jednorazowo)

Skrypt streamuje parquet (nie ładuje 31 GB do RAM), filtruje wyroki, pisze jeden JSON per wyrok
w formacie `StoredRawDocument` do `{RawStore:RootPath}/NSA/`. Idempotentny (pomija już zapisane →
można wznawiać). Uruchom tam, gdzie jest magazyn surowych (ta sama ścieżka co `RawStore:RootPath`
używana przez workera ingestii).

```bash
pip install datasets pyarrow
# smoke (kilkaset wyroków) — weryfikacja end-to-end zanim ruszysz całość:
python tools/nsa-ingest/fetch_nsa_wyroki.py --raw-root /sciezka/do/raw-store --limit 300
# pełny backfill:
python tools/nsa-ingest/fetch_nsa_wyroki.py --raw-root /sciezka/do/raw-store
```
Uwaga: pobranie danych z HF (host US) to jednorazowe ściągnięcie PUBLICZNYCH orzeczeń — runtime
produktu pozostaje PL/UE. Kwestie prawne (CC BY, nota CBOSA) czekają na bramkę 0.5 u prawnika;
budowa korpusu lokalnie (bez deployu) jest z tym spójna.

## Krok 2 — process (embedding, .NET, istniejący pipeline)

```bash
Ingestion__Source=NSA \
Ingestion__Mode=process \
Ingestion__ProcessParallelism=8 \
Embeddings__MaxBatch=256 \
dotnet run -c Release --project src/PrawoRAG.Ingestion
```
- `NsaNormalizer` (selektor `nsa-judgment` → zapis jako `judgment` = orzecznictwo) parsuje metadane
  z `SourcePayload`, `full_text` jako treść, sekcje sentencja/uzasadnienie, nagłówek kontekstowy
  „sąd — sygnatura" na każdym chunku.
- Idempotencja: powtórny `process` pomija zaindeksowane (fast-skip). Zmiana normalizera/modelu →
  re-process bez ponownego fetchu.
- Postęp co 50 dokumentów w logu; przy masowym runie odpalać w `screen`/`tmux`/nohup.

## Krok 3 — reprocessing błędów (jeśli wystąpią)

```bash
Ingestion__Source=NSA Ingestion__Mode=reprocess-failed dotnet run -c Release --project src/PrawoRAG.Ingestion
```
Wypisze rozkład powodów i ponowi tylko Failed (czyta po id, bez enumeracji całości).

## Weryfikacja jakości (przed pełnym runem)

Na próbce (`--limit 300` + process): sprawdź w bazie, że dokumenty mają sensowne chunki, sygnatury
i daty; odpal kilka pytań administracyjnych w czacie i zobacz, czy wyroki NSA/WSA wchodzą do źródeł.

## Znane ograniczenia / do decyzji

- **Świeżość**: dataset ma stan do ~2025-03 (raw). Delta (crawler CBOSA `rodzaj=wyrok` albo kolejne
  wydania JuDDGES) — osobny klocek; wniosek o API/ISP do NSA równolegle.
- **Skala indeksu**: ~8 mln nowych chunków dokłada się do obecnych 7,6 mln → ~15–16 mln, ~35–40 GB.
  To WIĘCEJ niż 32 GB RAM 3060 → indeks HNSW częściowo z dysku (jak dziś przy 18 GB). Do obserwacji;
  jeśli zaboli query-time, wtedy IVFFlat/kwantyzacja (osobny temat — Problem B analizy CBOSA).
- **Dokładny udział wyroków** policzy fetch (licznik `written` vs `skip(type)`).
