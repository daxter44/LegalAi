# Runbook: provider LLM (endpoint OpenAI-compatible, PL/UE)

Decyzja: **żadnych amerykańskich API LLM w funkcji produktu** (patrz pamięć/plan strategii). Domyślny
`Llm:Provider` w `appsettings.json` to **`local`** (`OpenAiCompatibleLlmProvider`) — ten sam kontrakt
obsługuje Ollama/llama.cpp lokalnie oraz każdy endpoint zgodny z OpenAI (OpenRouter, CloudFerro Sherlock).
Ścieżka Claude pozostaje w kodzie, ale NIE jest już domyślna.

Model launchowy: **Gemma ~31B** (testy Bielika wypadły słabo — bez trybu rozumowania nie dawał sensownych
odpowiedzi na czacie prawnym). Wybór modelu per rola potwierdzać evalem (`--exam`/`--chat`), nie preferencją.

## Dev / alfa — Gemma przez OpenRouter (most tymczasowy)
Klucz i dokładny id modelu podawać przez env (NIE commitować). Przykład:
```bash
Llm__Provider=local \
Llm__Local__BaseUrl=https://openrouter.ai/api/v1 \
Llm__Local__Model=<id-modelu-gemma-na-openrouter> \
Llm__Local__ApiKey=$OPENROUTER_API_KEY \
dotnet run --project src/PrawoRAG.Api
```
`OpenAiCompatibleLlmProvider` wysyła `ApiKey` jako `Authorization: Bearer` (obsłużone).

**UWAGA suwerenność:** OpenRouter routuje przez infrastrukturę US → dopuszczalne WYŁĄCZNIE w dev i
przyjaznej alfie (uprzedzeni testerzy, pytania abstrakcyjne). **Poza ścieżką zewnętrznych testerów**,
bo łamie główną obietnicę produktu. Ostrzeżenie o danych osobowych (P0-2) dodatkowo zniechęca do wklejania
danych klienta w tym oknie.

## Prod — Gemma na CloudFerro Sherlock (PL-hosted, docelowo)
Ta sama konfiguracja, inny `BaseUrl`/klucz (endpoint OpenAI-compatible Sherlocka; €0,56/1M in/out).
Status Gemmy ~31B na Sherlocku: **„wkrótce"** — to zależność zewnętrzna blokująca betę z obcymi
(dopytać CloudFerro o termin; ewentualny fallback: inny EU-hostowany endpoint Gemma). Bielik 11B / PLLuM
12B są na Sherlocku dostępne teraz, gdyby trzeba było odblokować betę wcześniej — ale decyzja modelowa to Gemma.

## Uruchomienie offline (Ollama) — bez sieci
Domyślne `Llm:Local` (`http://localhost:11434/v1`) działa z lokalną Ollamą, gdy trzeba pracować bez
żadnego API.
