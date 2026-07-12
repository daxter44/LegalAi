# RUNBOOK: Maszyna z RTX 3060 — Docker (TEI CUDA + Postgres/pgvector)

Cel: wystawić na tej maszynie **TEI (embedding na GPU)** i **Postgres+pgvector** (oba w Dockerze,
dostępne po sieci LAN), żeby `Ingestion__Mode=process` odpalany z MacBooka mógł się do nich podłączyć
zdalnie — surowe dane (`data/raw/`) zostają na MacBooku, nic się tu nie kopiuje.

Zakłada, że Docker jest już zainstalowany na tej maszynie. Nie zakłada, które OS — kroki 1-2 różnią się
dla Windows/Linux, reszta jest identyczna.

## 0. Model i wersje — nie zgadywać

- Model embeddingów: **`sdadas/mmlw-retrieval-roberta-large-v2`** (1024 wymiary) — zablokowany dla
  całego korpusu, zmiana = re-embedding wszystkiego. Nie podmieniać.
- Postgres: **`pgvector/pgvector:pg17`** — ten sam obraz co w `infra/compose.yaml` na MacBooku (spójność
  schematu/rozszerzenia).
- TEI: obraz CUDA dopasowany do architektury karty. **RTX 3060 to Ampere (compute capability 8.6)** —
  tag **`ghcr.io/huggingface/text-embeddings-inference:86-latest`** (ten sam tag co RTX 3090/A4000).
  Zły tag = błąd przy starcie kontenera, nie cichy fallback na CPU.

## 1. Sterowniki NVIDIA + GPU w Dockerze

**Windows** (Docker Desktop):
- Zainstaluj najnowszy sterownik GeForce (obsługuje WSL2 CUDA passthrough).
- W Docker Desktop: Settings → Resources → WSL Integration — upewnij się, że silnik WSL2 jest włączony.
  Nowsze wersje Docker Desktop wykrywają GPU NVIDIA automatycznie, osobny toolkit nie jest potrzebny.

**Linux natywnie**:
```bash
# Sterownik NVIDIA musi już działać — sprawdź: nvidia-smi
# Doinstaluj NVIDIA Container Toolkit (jeśli jeszcze nie ma):
distribution=$(. /etc/os-release; echo $ID$VERSION_ID)
curl -s -L https://nvidia.github.io/nvidia-docker/gpgkey | sudo apt-key add -
curl -s -L https://nvidia.github.io/nvidia-docker/$distribution/nvidia-docker.list | \
  sudo tee /etc/apt/sources.list.d/nvidia-docker.list
sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit
sudo systemctl restart docker
```

**Weryfikacja (oba OS) — zrób to PRZED dalszymi krokami:**
```bash
docker run --rm --gpus all nvidia/cuda:12.4.0-base-ubuntu22.04 nvidia-smi
```
Musisz zobaczyć tabelkę z RTX 3060. Jeśli błąd — nie idź dalej, napraw sterowniki/toolkit najpierw.

## 2. Firewall — otwórz porty 5432 (Postgres) i 8080 (TEI)

**Windows**: Zapora systemu Windows → reguła przychodząca, TCP, porty 5432 i 8080, zakres = sieć lokalna
(nie "dowolny adres" — to nie ma być publicznie dostępne).

**Linux (ufw)**:
```bash
sudo ufw allow from 192.168.0.0/16 to any port 5432 proto tcp
sudo ufw allow from 192.168.0.0/16 to any port 8080 proto tcp
```
(Dopasuj zakres `192.168.0.0/16` do swojej faktycznej podsieci domowej.)

## 3. Docker Compose — TEI + Postgres

Utwórz katalog roboczy i plik `compose.yaml`:

```yaml
name: praworag-3060

services:
  db:
    image: docker.io/pgvector/pgvector:pg17
    environment:
      POSTGRES_USER: praworag
      POSTGRES_PASSWORD: praworag
      POSTGRES_DB: praworag
    ports:
      - "0.0.0.0:5432:5432"   # nasłuch na wszystkich interfejsach — dostępny z LAN
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U praworag -d praworag"]
      interval: 5s
      timeout: 5s
      retries: 10

  tei:
    image: ghcr.io/huggingface/text-embeddings-inference:86-latest
    command: ["--model-id", "sdadas/mmlw-retrieval-roberta-large-v2", "--port", "80",
              "--auto-truncate", "--max-client-batch-size", "256"]
    environment:
      HUGGINGFACE_HUB_CACHE: /data
    ports:
      - "0.0.0.0:8080:80"     # jw. — nasłuch na wszystkich interfejsach
    volumes:
      - tei-cache:/data
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]

volumes:
  pgdata:
  tei-cache:
```

Uwaga: `deploy.resources.reservations.devices` to składnia Compose v2 dla GPU — jeśli Twoja wersja
Dockera jej nie honoruje, alternatywa to `docker run --gpus all` bezpośrednio dla `tei` (patrz krok 3b).

Odpal:
```bash
docker compose up -d
docker compose logs -f tei   # obserwuj aż zobaczysz "Ready" — pierwsze pobranie modelu (~1,4 GB) chwilę trwa
```

### 3b. Jeśli `deploy.devices` nie zadziała (starszy Docker Compose)

Zamiast sekcji `tei` w compose, odpal ją osobno przez `docker run`:
```bash
docker run -d --name tei --gpus all -p 0.0.0.0:8080:80 \
  -v tei-cache:/data -e HUGGINGFACE_HUB_CACHE=/data \
  ghcr.io/huggingface/text-embeddings-inference:86-latest \
  --model-id sdadas/mmlw-retrieval-roberta-large-v2 --port 80 \
  --auto-truncate --max-client-batch-size 256
```
(`db` może zostać w compose bez zmian — nie potrzebuje GPU.)

## 4. Weryfikacja lokalna (na maszynie z 3060)

```bash
curl http://localhost:8080/health   # 200 OK
curl -s -X POST http://localhost:8080/embed \
  -H 'Content-Type: application/json' \
  -d '{"inputs":["zapytanie: test"],"normalize":true}' | head -c 200

docker exec -it $(docker compose ps -q db) psql -U praworag -d praworag -c '\dx'
# powinno pokazać rozszerzenie vector (albo pustą listę — pgvector doinstaluje się migracją EF)
```

**Sprawdź, że TEI faktycznie chodzi na GPU, nie CPU** — w logu kontenera szukaj linii ze wzmianką
o CUDA/urządzeniu GPU (nie `Cpu`). Jeśli widzisz CPU mimo tagu `:86-latest`, GPU passthrough
nie zadziałał — wróć do kroku 1.

## 5. Znajdź IP tej maszyny w sieci LAN

```bash
# Linux:
ip addr show | grep "inet 192\|inet 10\."
# Windows (w PowerShell):
ipconfig | findstr IPv4
```
Zanotuj adres — u nas to **`192.168.100.11`**.

## 6. Z MacBooka: nakładka schematu na nowego Postgresa

Baza jest pusta — trzeba wgrać migracje EF Core zanim `process` będzie miał gdzie zapisywać:
```bash
ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag" \
  dotnet ef database update --project src/PrawoRAG.Storage
```

## 7. Zdejmij ciężkie indeksy PRZED masowym ładowaniem

Tabela `chunks` ma dwa indeksy, które przy insercie pojedynczo (a robimy ich ~16 mln) potrafią
drastycznie spowolnić `process`: **HNSW** na wektorach (buduje graf przy każdym wierszu) i **GIN**
na pełnotekstowym wyszukiwaniu. Reszta indeksów (btree, w tym unikalne ograniczenia pilnujące
idempotencji) zostaje bez zmian — są tanie i część z nich chroni przed duplikatami w trakcie ładowania.

```bash
docker exec -it $(docker compose ps -q db) psql -U praworag -d praworag -c '
DROP INDEX IF EXISTS "IX_chunks_Embedding";
DROP INDEX IF EXISTS "IX_chunks_SearchVector";
'
```

To bezpieczne i odwracalne — usuwa tylko strukturę przyspieszającą wyszukiwanie, dane (wektory, tekst)
zostają nietknięte. EF Core pamięta zastosowane migracje po nazwie, nie porównuje żywego schematu,
więc ten ręczny drop nie pomyli przyszłego `dotnet ef database update`.

**Dopóki nie odbudujesz (krok 9) — wyszukiwanie semantyczne/pełnotekstowe jest praktycznie nieużywalne**
(sequential scan po milionach wierszy). Nie testuj czatu/retrievalu w tym oknie.

## 8. Z MacBooka: test na próbce, potem pełny przebieg

Zgodnie z zasadą z `RUNBOOK-EMBEDDING-ZDALNY.md` — zawsze najpierw mała próbka:
```bash
Embeddings__BaseUrl=http://192.168.100.11:8080 \
Embeddings__MaxBatch=256 \
ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag" \
Ingestion__Mode=process \
Ingestion__MaxItems=500 \
dotnet run --project src/PrawoRAG.Ingestion
```
Zmierz czas → ekstrapoluj na cały korpus (~16 mln chunków, patrz szacunek w rozmowie roboczej —
~296 GB w Postgresie). Jeśli ETA sensowne, powtórz **bez `Ingestion__MaxItems`**, w `tmux`/`screen`
z logowaniem do pliku (`2>&1 | tee logs/process-$(date +%Y%m%d-%H%M).log`) — pełny przebieg to godziny,
terminal SSH/zdalny pulpit nie może się rozłączyć w trakcie.

## 9. Po zakończeniu pełnego przebiegu: odbuduj indeksy

```bash
docker exec -it $(docker compose ps -q db) psql -U praworag -d praworag -c '
SET maintenance_work_mem = '"'"'4GB'"'"';
CREATE INDEX "IX_chunks_Embedding" ON chunks USING hnsw ("Embedding" vector_cosine_ops);
CREATE INDEX "IX_chunks_SearchVector" ON chunks USING gin ("SearchVector");
'
```
`maintenance_work_mem` przyspiesza budowę HNSW — podnieś, jeśli maszyna ma więcej wolnego RAM-u.
**Odbudowa HNSW przy 16 mln wektorów zajmie realnie godziny, nie minuty** — to osobny, długi krok,
zaplanuj go świadomie (noc, `tmux`), zanim uznasz proces za zakończony i przejdziesz do E5 (krok 12).

## 10. Awaria i wznowienie

Mechanizmy `docs/PLAN-ODPORNOSC-INGESTU.md` działają identycznie zdalnie: przerwany `process` wznawia
się **tym samym poleceniem** (fast-skip pomija już gotowe dokumenty w minuty, nie godziny). Pełna
diagnostyka porażek — patrz sekcja „Awaria i wznowienie" w `docs/RUNBOOK-EMBEDDING-ZDALNY.md`, dotyczy
1:1 tego wariantu (ten sam kod `RawProcessRunner`/`IngestionPipeline`, tylko `Embeddings__BaseUrl`
i `ConnectionStrings__Db` wskazują na maszynę z 3060 zamiast `localhost`).

## 11. Higiena wielogodzinnego przebiegu

- Maszyna z 3060: zasilanie sieciowe, uśpienie wyłączone (Windows: Ustawienia → System → Zasilanie →
  „Nigdy" dla uśpienia; Linux: `systemctl mask sleep.target suspend.target`).
- MacBook (skąd leci `process`): `caffeinate -i` na czas przebiegu, żeby nie usnął w trakcie.
- Oba na tym samym LAN (Wi-Fi wystarczy, ale kabel pewniejszy przy wielogodzinnym transferze).

## 12. Po zakończeniu — walidacja i porządki

1. **E5 na pełnym korpusie** (z MacBooka, wskazując na zdalną bazę, PO odbudowie indeksów z kroku 9):
   ```bash
   ConnectionStrings__Db="Host=192.168.100.11;..." dotnet run --project src/PrawoRAG.Eval
   ```
2. Gdy wynik OK — możesz zgasić `tei` na 3060 (Postgres tam zostaje jako docelowa baza, albo zrób
   `pg_dump` i przenieś dalej, wg `RUNBOOK-EMBEDDING-ZDALNY.md` §K4).
3. Firewall: jeśli maszyna z 3060 ma zostać w sieci na dłużej, rozważ zawężenie reguł z kroku 2 tylko
   do konkretnego IP MacBooka zamiast całej podsieci.
