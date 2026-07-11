# RUNBOOK: Zdalny embedding pełnego korpusu (RunPod / GPU VM)

Data: 2026-07-08. Realizuje krok K2–K4 z `PLAN-STRATEGIA-PILOTAZ.md` §3.2.
Cel: raz a dobrze zembeddować pełny korpus na wynajętym GPU, z datą migawki, i przywieźć gotowe artefakty.

## Warunki wstępne (bez nich nie ma czego liczyć)
1. **Ukończony fetch (K0/K1)**: pełny magazyn surowych danych w `data/raw` (`RawStore:RootPath`).
   Fetch to tylko wywołania API — GPU nic tu nie przyspieszy; robimy go wcześniej, lokalnie/na tanim VPS.
2. **Potwierdzony model embeddingów** (`sdadas/mmlw-retrieval-roberta-large-v2`) — decyzja nieodwracalna
   na życie korpusu (zmiana = re-embedding całości).
3. Zanotowana **data migawki**: `JudgmentDateFrom→dziś` (SAOS) + data discovery ELI z dnia fetchu.

## Jak działa nasz pipeline (fakty z kodu, istotne dla wyboru wariantu)
- `Ingestion:Mode=process` czyta surowe z magazynu (offline), chunkuje, embedduje, zapisuje do Postgresa
  (`src/PrawoRAG.Ingestion/Program.cs`). Idempotentne — przerwany przebieg wznawiamy tym samym poleceniem,
  przetworzone dokumenty są pomijane (fast-skip: minuty, nie godziny — patrz „Awaria i wznowienie" niżej).
- Klient TEI (`TeiEmbeddingProvider.cs`) wysyła batche ≤ `Embeddings:MaxBatch` (domyślnie 32 — twardy limit
  TEI CPU; TEI GPU przyjmie więcej, ale trzeba podnieść PO OBU stronach: env `Embeddings__MaxBatch`
  u nas i flaga `--max-client-batch-size` w TEI).
- Chunker liczy tokeny przez TEI `/tokenize` — na dokument przypada WIELE wywołań HTTP, dokumenty idą
  sekwencyjnie. **Wniosek: odległość sieciowa worker↔TEI ma ogromny wpływ na czas.** Im bliżej siebie, tym lepiej.

## Dobór GPU: VRAM nie gra roli, cena za godzinę tak
Model embeddingowy (roberta-large, ~355 mln parametrów) zajmuje ~0,7 GB w FP16; z batchem 256×512 tokenów
całość mieści się w 2–4 GB VRAM. **Każda karta ≥8 GB ma zapas** — nie płacić za VRAM ani za topową
architekturę. Kolejność opłacalności (ceny RunPod, orientacyjnie): RTX A4000 16 GB (~0,10–0,15 USD/h)
≥ RTX 3090/3090 Ti (~0,17 USD/h) > RTX 4090 (~0,34 USD/h — szybsza ~1,5–2×, ale 2× droższa).
GPU prawdopodobnie i tak nie będzie wąskim gardłem (sekwencyjny pipeline + sieć/CPU) — w trakcie próbki
sprawdzić wykorzystanie GPU (`nvidia-smi`/dashboard); poniżej ~50% = można brać jeszcze tańszą kartę.
**Obraz TEI musi pasować do architektury karty**: `:86-latest` dla Ampere (RTX 3090/3090 Ti/A4000),
`:89-latest` dla Ady (RTX 4090/L4), bez sufiksu dla A100. Zły obraz = błąd przy starcie poda.
Uwaga: to wymiarowanie dotyczy TYLKO sesji embeddingu; GPU produkcyjne pod Bielika 11B (~8 GB wag,
potrzeba ~16–20 GB VRAM) to osobna, niezależna decyzja — patrz `PLAN-STRATEGIA-PILOTAZ.md` §3.1.

## ZASADA NADRZĘDNA: najpierw próbka, potem całość
W każdym wariancie pierwszy przebieg z `Ingestion__MaxItems=500`. Zmierzyć czas → ekstrapolacja na pełny
korpus (~551 tys. orzeczeń + ~14 tys. aktów). Dopiero gdy ETA jest akceptowalne — zdjąć limit.
Płacimy za godziny, więc 15 minut pomiaru to najtańsze ubezpieczenie planu.

---

## Wariant 0 — ZACZNIJ TUTAJ: domowy PC z RTX 3060 laptop (6 GB) — koszt 0 zł

6 GB VRAM wystarcza z zapasem (model ~0,7 GB + bufory przy batchu 64). Laptopowa 3060 jest wolniejsza
od wynajmowanych kart, ale jeśli wąskim gardłem jest sekwencyjny pipeline, różnicy nie będzie widać —
a sieć domowa (LAN) eliminuje problem rozmowności `/tokenize`, który w wariancie A bywa zabójczy.

1. Na PC: Docker Desktop (WSL2, wsparcie GPU NVIDIA włączone). TEI:
   ```
   docker run --gpus all -p 8080:80 ghcr.io/huggingface/text-embeddings-inference:86-latest \
     --model-id sdadas/mmlw-retrieval-roberta-large-v2 --auto-truncate --max-client-batch-size 64
   ```
2. Na MacBooku (Postgres z compose jak zwykle):
   `Embeddings__BaseUrl=http://<IP-PC-w-LAN>:8080  Embeddings__MaxBatch=64  Ingestion__Mode=process
   Ingestion__MaxItems=500  dotnet run --project src/PrawoRAG.Ingestion` → pomiar → bez limitu, w tmux.
3. Higiena wielogodzinnego przebiegu: laptop na zasilaniu, dobra wentylacja (throttling tylko spowalnia),
   usypianie wyłączone na OBU maszynach (`caffeinate` na Macu).
4. Wektory lądują od razu w lokalnym Postgresie — test lokalny gratis; artefakty (K4) robisz z własnej bazy.
5. Kryterium przejścia na plan B (RunPod/VM): ekstrapolacja z próbki > ~3–4 doby albo problemy termiczne.

## Wariant A — RunPod: na GPU tylko TEI, reszta zostaje u Ciebie

RunPod wynajmuje **kontenery, nie maszyny wirtualne** — nie postawisz tam całego stosu przez
`docker compose` (brak demona Dockera w podzie). Naturalny podział: TEI jako pod na GPU,
Postgres + worker ingestii lokalnie na MacBooku.

**Zalety**: zero instalowania czegokolwiek zdalnie; wektory lądują od razu w Twoim lokalnym Postgresie
(test lokalny „gratis", bez ściągania dumpa). **Ryzyko**: każde `/embed` i `/tokenize` leci przez internet —
RTT może zdominować czas; werdykt wyda próbka 500 dokumentów.

1. Konto na runpod.io, doładowanie ~10–25 USD.
2. Deploy → Pods → GPU wg sekcji „Dobór GPU" powyżej (np. **RTX 3090 Ti ~0,17 USD/h** albo tańsza
   A4000; Secure Cloud stabilniejszy od Community).
3. Konfiguracja poda (Custom template):
   - Container Image dopasowany do karty: `ghcr.io/huggingface/text-embeddings-inference:86-latest`
     (Ampere: 3090/3090 Ti/A4000) lub `:89-latest` (Ada: 4090/L4); dokładne tagi w dokumentacji TEI.
   - Container Start Command (args): `--model-id sdadas/mmlw-retrieval-roberta-large-v2 --auto-truncate
     --max-client-batch-size 256 --port 80`
   - Expose HTTP Ports: `80`. Dysk kontenera: 20 GB (model waży ~1,4 GB, pobiera się przy starcie, 2–3 min).
4. Po starcie pod dostaje URL: `https://<POD_ID>-80.proxy.runpod.net`. Test z MacBooka:
   ```bash
   curl -s -X POST https://<POD_ID>-80.proxy.runpod.net/embed \
     -H 'Content-Type: application/json' \
     -d '{"inputs":["zapytanie: test"],"normalize":true}' | head -c 200
   ```
5. Lokalnie (Postgres z `infra/compose.yaml` działa jak zwykle):
   ```bash
   Embeddings__BaseUrl=https://<POD_ID>-80.proxy.runpod.net \
   Embeddings__MaxBatch=256 \
   Ingestion__Mode=process \
   Ingestion__MaxItems=500 \
   dotnet run --project src/PrawoRAG.Ingestion   # próbka → pomiar → potem bez MaxItems
   ```
6. Pełny przebieg w `tmux`/`caffeinate` (MacBook nie może uśpić się w trakcie). Przerwanie = wznowienie
   tym samym poleceniem (idempotencja).
7. Po zakończeniu: **Terminate** poda (samo „Stop" wciąż nalicza za storage). URL poda jest publiczny
   i bez uwierzytelnienia — żyje godziny, ryzyko znikome, ale nie publikować go nigdzie.

## Wariant B — godzinowa maszyna GPU z pełnym dostępem (gdy próbka w A wypadnie źle)

Dostawcy dający **pełny VM z rootem i dockerem** rozliczany godzinowo: DataCrunch, Lambda, OVH, Scaleway
(sprawdzić aktualne ceny/dostępność; RunPod odpada — patrz wyżej). Cały stos stoi obok siebie,
sieć przestaje być wąskim gardłem.

1. VM: RTX 4090 / L40S / A100, Ubuntu 22.04+ ze sterownikami NVIDIA, ≥200 GB NVMe.
2. Instalacja: `docker` + compose plugin + NVIDIA container toolkit, .NET 10 SDK, `git clone` repo
   (albo rsync katalogu źródeł).
3. `infra/compose.yaml`: podmienić obraz TEI na GPU (`:89-latest` dla Ady) i dodać dostęp do GPU
   (`gpus: all`); dopisać flagę `--max-client-batch-size 256`; odkomentować drugą instancję TEI
   (reranker) — przyda się w kroku 7.
4. Wgrać surowe dane: `rsync -avz --progress data/raw/ user@vm:/opt/praworag/data/raw/`
   (kilkanaście–kilkadziesiąt GB; przy 100 Mbps uploadu ~1–2 h na 50 GB).
5. Przebieg jak w wariancie A pkt 5 (BaseUrl=http://localhost:8080), w `tmux`. Najpierw próbka 500.
   Jeśli GPU się nudzi, a CPU jest wąskim gardłem (sekwencyjne parsowanie) — trudno, godziny są tanie;
   ewentualna równoległość w `RawProcessRunner` to zmiana w kodzie do osobnej decyzji.
6. (Opcjonalna optymalizacja masowego ładowania) Indeks HNSW istnieje od migracji `InitialSchema`,
   więc każdy insert dokłada do niego na bieżąco. Przy milionach wektorów szybciej: przed przebiegiem
   `DROP INDEX <nazwa_indeksu_hnsw>`, po przebiegu `SET maintenance_work_mem='8GB'; CREATE INDEX ...`
   (definicja w `src/PrawoRAG.Storage/Migrations/20260611111624_InitialSchema.cs`). Czysto operacyjne,
   odwracalne.
7. **Walidacja ZANIM zgasisz maszynę (K3)**: E5 retrieval na pełnym korpusie
   (`dotnet run --project src/PrawoRAG.Eval` z env wskazującymi lokalną bazę/TEI na VM) + ponowny test
   rerankera. Wynik do `docs/EVAL-LOG.md`. Poprawki robimy na tej samej maszynie, bez drugiego wynajmu.
8. **Artefakty (K4)** — oba, do taniego object storage (B2/R2), potem na dysk lokalny:
   - `pg_dump -Fc praworag > korpus-<data-migawki>.dump` — przenośny wszędzie; restore odbudowuje
     indeks HNSW od zera (na MacBooku: przez noc);
   - `docker compose stop postgres && tar czf pgdata-<data-migawki>.tgz <wolumen>` — restore w minuty,
     ale tylko na Linux x86_64 z tym samym obrazem `pgvector/pgvector:pg17` (→ serwer produkcyjny).
9. Terminate VM. Od tej chwili każdy kolejny serwer dostaje gotowy korpus z artefaktu — bez GPU.

## Awaria i wznowienie (ODP — dotyczy każdego wariantu)

Mechanizmy z `docs/PLAN-ODPORNOSC-INGESTU.md` (kod: `RawProcessRunner`/`IngestionPipeline`):

**Zawsze loguj do pliku** — konsola po nocnym runie znika:
```bash
... dotnet run --project src/PrawoRAG.Ingestion 2>&1 | tee logs/process-$(date +%Y%m%d-%H%M).log
```

**Gdy run padnie w środku nocy:**
1. Przeczytaj OSTATNIE linie logu. Komunikat `Bezpiecznik: N porażek z rzędu…` = awaria
   infrastruktury (padły kontener TEI, sieć do PC, Postgres) — NIE złe dokumenty. Sprawdź:
   `docker ps` / `docker logs` na maszynie z TEI, `curl http://<IP>:8080/health`, kontener Postgresa.
2. Napraw przyczynę i uruchom **to samo polecenie**. Na starcie zobaczysz
   `Fast-skip <źródło>: N dokumentów już zaindeksowanych…` — przewinięcie gotowych kosztuje
   ~1–3 ms/dokument (odczyt pliku + hash, zero zapytań do bazy), czyli minuty przy setkach tysięcy.
3. Dokumenty oznaczone `Failed` w czasie awarii NIE są w zbiorze fast-skip → przetworzą się od nowa
   same. Zero ręcznego sprzątania.

**Diagnoza pojedynczych porażek (bez ponownego runu):**
- log: `Porażka #59584: SAOS/12345 [embed] <przyczyna>` — pozycja, dokument, etap
  (`lookup/normalize/chunk/embed/db-write/re-embed`);
- pełne błędy ze stack trace: `logs/process-failures-<źródło>-<timestamp>.jsonl`
  (podgląd: `jq -r '[.seq,.externalId,.stage,.error] | @tsv' <plik> | head`);
- stan w bazie: `SELECT "ExternalId", "FailureReason", "AttemptCount" FROM documents
  WHERE "Status" = 6;` (6 = Failed; `FailureReason` = `[etap] przyczyna`, ucięty do 1000 znaków
  — pełna wersja w JSONL);
- plik surowy dokumentu: `data/raw/<źródło>/<ExternalId z / zamienionym na _>.json`.

**Konfiguracja (env):** `Ingestion__FastSkip` (default `true`; kill-switch), `Ingestion__FailStreakLimit`
(default `10`; `0` wyłącza bezpiecznik), `Ingestion__FailureLogDir` (default `logs`).

## Budżet i czas (do zweryfikowania próbką)
- GPU-godziny: przy 3090 Ti/A4000 (~0,10–0,17 USD/h) realny budżet całości **5–20 USD (~20–80 zł)**, z zapasem.
- Sam embedding 4,9 mln fragmentów to (przy dobrym batchowaniu) pojedyncze godziny; niewiadomą
  jest narzut parsowania/chunkowania i sieci — dlatego próbka 500 dokumentów przed zdjęciem limitu.
- Koszt przechowywania artefaktów (B2/R2): grosze/mies.
