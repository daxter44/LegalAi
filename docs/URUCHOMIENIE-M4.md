# Uruchomienie na MacBooku M4 — pełny RAG lokalnie (poziom 3)

Cel: zweryfikować cały łańcuch **bez chmury** — `fetch` → `process` (TEI/mmlw) → `/api/chat`
z **Bielikiem lokalnie** (pakiet „Diamond": dane nie opuszczają maszyny). Architektura:
[USTALENIA-I-ODKRYCIA.md](USTALENIA-I-ODKRYCIA.md), plan: [PLAN.md](PLAN.md).

## 0. Narzędzia (raz)
```bash
brew install --cask dotnet-sdk          # .NET 10 SDK (dotnet --version → 10.x)
brew install podman                     # albo Docker Desktop
podman machine init && podman machine start
brew install ollama
ollama serve &                          # serwer LLM na :11434 (OpenAI-compatible)
```

## 1. Bielik w Ollamie
```bash
# Bielik v3.0 Instruct jest wprost w rejestrze Ollamy (Q5_K_M = 7,9 GB, dobry balans).
# UWAGA: wariant "DFlash" NIE ma GGUF → nie zadziała w Ollamie. Używamy Instruct v3.0.
ollama pull SpeakLeash/bielik-11b-v3.0-instruct:Q5_K_M
ollama list                                                # potwierdź nazwę → to Llm:Local:Model
```
Kwantyzacje do wyboru: `Q4_K_M` (6,7 GB, najbezpieczniejszy przy 16 GB RAM), `Q5_K_M` (7,9 GB),
`Q6_K` (9,2 GB), `Q8_0` (12 GB). Cokolwiek pobierzesz, tę samą nazwę wstaw w kroku 6.

## 2. Klon repo
```bash
git clone https://github.com/daxter44/LegalAi.git
cd LegalAi
```

## 3. Infra: Postgres + TEI (embeddingi)
```bash
cd infra && podman compose up -d && cd ..
podman logs -f praworag-tei-1     # poczekaj na "Ready" (TEI ściąga mmlw ~0,5-1 GB); Ctrl+C
curl http://localhost:8080/health # 200 = OK
```
⚠️ **Główne ryzyko na Apple Silicon:** obraz TEI `cpu-latest` bywa amd64 → poleci pod emulacją
(wolno) albo nie wstanie. Jeśli `/health` milczy — potrzebny fallback (natywny embedding).

## 4. Schemat bazy
```bash
dotnet tool restore
dotnet ef database update --project src/PrawoRAG.Storage
```

## 5. Pobierz + przetwórz próbkę
```bash
# fetch — surowe do data/raw (sieć do SAOS; pierwszy search ~14s, potem szybko)
Ingestion__Mode=fetch Ingestion__MaxItems=50 dotnet run --project src/PrawoRAG.Ingestion

# process — chunk + embedding (TEI) → Postgres. OFFLINE, powtarzalne po zmianach kodu.
Ingestion__Mode=process dotnet run --project src/PrawoRAG.Ingestion
```
Do oceny jakości potem zwiększ `MaxItems` (np. 300–500).

## 6. API z lokalnym Bielikiem
```bash
Llm__Provider=local \
Llm__Local__Model="SpeakLeash/bielik-11b-v3.0-instruct:Q5_K_M" \
ASPNETCORE_URLS=http://localhost:5005 \
dotnet run --project src/PrawoRAG.Api
```

## 7. Weryfikacja
```bash
# Poziom 2 — retrieval (bez LLM): czy wracają trafne orzeczenia?
curl -s localhost:5005/api/search -H 'content-type: application/json' \
  -d '{"query":"wyłączenie sędziego od rozpoznania sprawy"}' | jq

# Poziom 3 — chat z Bielikiem (SSE): ugruntowana odpowiedź + cytaty
curl -N localhost:5005/api/chat -H 'content-type: application/json' \
  -d '{"question":"Kiedy sąd wyłącza sędziego na wniosek strony?"}'
```

## Na co patrzeć (i czego NIE oczekiwać)
- **`/api/search`** — czy zwrócone orzeczenia dotyczą pytania. To rdzeń jakości (dźwignia = retrieval).
- **`/api/chat`** — czy Bielik trzyma się kontekstu i cytuje `[n]` z sygnaturami; event `done` niesie
  `citationCheck` (anty-fabrykacja).
- **NIE oceniaj abstynencji** — próg 0.55 na surowym cosine jeszcze nie rozróżnia „wiem/nie wiem"
  (kalibracja w E5). Pytanie spoza wycinka (apelacyjne 2023+) może zwrócić śmieci — to nie błąd, to wycinek.

## Uwagi
- **Magazyn surowych** ląduje w `src/PrawoRAG.Ingestion/data/raw/` (bo `dotnet run` ustawia CWD na katalog
  projektu). Jest gitignorowany. Na inną lokalizację ustaw `RawStore__RootPath` (ścieżka absolutna).
- **Przenośność korpusu:** `data/raw/` można skopiować między maszynami (zip/rsync) — `process` odtworzy
  bazę bez ponownego pobierania z SAOS.
- **Re-chunk po zmianie kodu:** `process` pomija dokumenty o niezmienionym `content_hash`. Aby wymusić
  przeliczenie: wyczyść dokumenty źródła w bazie (lokalnie, bez pobierania) i uruchom `process` ponownie.
