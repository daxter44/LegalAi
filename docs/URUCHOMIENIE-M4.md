# Uruchomienie na MacBooku M4 — pełny RAG lokalnie (poziom 3)

Cel: zweryfikować cały łańcuch **bez chmury** — `fetch` → `process` (TEI/mmlw) → `/api/chat`
z **Bielikiem lokalnie** (pakiet „Diamond": dane nie opuszczają maszyny). Architektura:
[USTALENIA-I-ODKRYCIA.md](USTALENIA-I-ODKRYCIA.md), plan: [PLAN.md](PLAN.md).

## 0. Narzędzia (raz)
```bash
brew install --cask dotnet-sdk          # .NET 10 SDK (dotnet --version → 10.x)
brew install podman                     # albo Docker Desktop — tylko pod Postgres (TEI patrz krok 3)
podman machine init && podman machine start
brew install ollama
ollama serve &                          # serwer LLM na :11434 (OpenAI-compatible)
brew install text-embeddings-inference  # TEI natywnie (Metal) — patrz krok 3, nie obraz Dockera
```

## 1. Bielik w Ollamie
```bash
ollama pull hf.co/speakleash/Bielik-11B-v3.0-DFlash-GGUF   # zweryfikuj dokładny tag na stronie HF
ollama list                                                 # ZAPAMIĘTAJ nazwę → to Llm:Local:Model
```
⚠️ Dokładny tag GGUF (może mieć sufiks kwantyzacji, np. `:Q4_K_M`) sprawdź na stronie modelu.
Cokolwiek pokaże `ollama list`, wstawiasz w kroku 6 jako `Llm__Local__Model`.

## 2. Klon repo
```bash
git clone https://github.com/daxter44/LegalAi.git
cd LegalAi
```

## 3. Infra: Postgres (kontener) + TEI (natywnie, Metal)
Obraz `ghcr.io/huggingface/text-embeddings-inference:cpu-latest` jest **tylko amd64** — na Apple
Silicon leci pod Rosettą (wolno) albo w ogóle nie wstaje. Zamiast tego uruchamiamy TEI natywną
binarką z Homebrew (krok 0), która ma pełne wsparcie Metal/GPU — szybciej i bez emulacji.

```bash
# Postgres+pgvector — tylko usługa `db` z compose (TEI już nie stawiamy w kontenerze)
cd infra && podman compose up -d db && cd ..

# TEI natywnie, w tle, na porcie 8080 (ten sam port, którego oczekuje appsettings.json)
HUGGINGFACE_HUB_CACHE=.local/tei-cache \
  text-embeddings-router --model-id sdadas/mmlw-retrieval-roberta-base --port 8080 --auto-truncate \
  > /tmp/tei.log 2>&1 &
disown

curl http://localhost:8080/health # 200 = OK
tail -f /tmp/tei.log              # szukaj "Starting Bert model on Metal(...)" i "Ready"; Ctrl+C
```
Weryfikacja, że faktycznie chodzi po GPU: w logu powinno być `Metal(MetalDevice(...))`, nie `Cpu`.

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
Llm__Local__Model="bielik:latest" \
dotnet run --project src/PrawoRAG.Api --no-launch-profile
```
⚠️ `applicationUrl` w `Properties/launchSettings.json` **nadpisuje** `ASPNETCORE_URLS` ustawione
w środowisku — bez `--no-launch-profile` API zawsze wystartuje na porcie z profilu (domyślnie
`5024`), niezależnie od tego, co ustawisz w zmiennej. Albo pomiń `ASPNETCORE_URLS` i używaj
`5024` (patrz log `Now listening on:`), albo dodaj `--no-launch-profile`, żeby własny URL zadziałał.
`Llm__Local__Model` musi być dokładną nazwą z `ollama list` (zob. krok 1) — na tej maszynie to
`bielik:latest`, nie pełny tag Hugging Face.

## 7. Weryfikacja
```bash
# Poziom 2 — retrieval (bez LLM): czy wracają trafne orzeczenia?
curl -s localhost:5024/api/search -H 'content-type: application/json' \
  -d '{"query":"wyłączenie sędziego od rozpoznania sprawy"}' | jq

# Poziom 3 — chat z Bielikiem (SSE): ugruntowana odpowiedź + cytaty
curl -N --max-time 240 localhost:5024/api/chat -H 'content-type: application/json' \
  -d '{"question":"Kiedy sąd wyłącza sędziego na wniosek strony?"}'
```
Generowanie 11B modelu strumieniuje token po tokenie i potrafi zająć 1-2 min na M4 — `--max-time`
zapobiega ucięciu przez domyślny timeout curla w trakcie ręcznego testu.

## Na co patrzeć (i czego NIE oczekiwać)
- **`/api/search`** — czy zwrócone orzeczenia dotyczą pytania. To rdzeń jakości (dźwignia = retrieval).
- **`/api/chat`** — czy Bielik trzyma się kontekstu i cytuje `[n]` z sygnaturami; event `done` niesie
  `citationCheck` (anty-fabrykacja).
- **NIE oceniaj abstynencji** — próg 0.55 na surowym cosine jeszcze nie rozróżnia „wiem/nie wiem"
  (kalibracja w E5). Pytanie spoza wycinka (apelacyjne 2023+) może zwrócić śmieci — to nie błąd, to wycinek.
- **Bielik częściej fabrykuje niż Claude** — na pierwszym smoke teście (2026-07) cytaty `[1][2][3]`
  były poprawne (`outOfRange: []`), ale Bielik dopisał do treści wymyślone sygnatury spraw spoza
  kontekstu → `citationCheck.isClean: false`. To nie błąd systemu — anty-fabrykacja to poprawnie
  wyłapała. Sygnał, że lokalny model 11B wymaga bardziej rygorystycznej weryfikacji `citationCheck`
  niż Claude, nie że pipeline jest zepsuty.

## Uwagi
- **Magazyn surowych** ląduje w `src/PrawoRAG.Ingestion/data/raw/` (bo `dotnet run` ustawia CWD na katalog
  projektu). Jest gitignorowany. Na inną lokalizację ustaw `RawStore__RootPath` (ścieżka absolutna).
- **Przenośność korpusu:** `data/raw/` można skopiować między maszynami (zip/rsync) — `process` odtworzy
  bazę bez ponownego pobierania z SAOS.
- **Re-chunk po zmianie kodu:** `process` pomija dokumenty o niezmienionym `content_hash`. Aby wymusić
  przeliczenie: wyczyść dokumenty źródła w bazie (lokalnie, bez pobierania) i uruchom `process` ponownie.
