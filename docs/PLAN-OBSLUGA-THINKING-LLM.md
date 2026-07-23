# TODO: obsługa „myślenia" (thinking/reasoning) providerów OpenAI-compatible

Data: 2026-07-23. Próba podjęta i WYCOFANA w tej samej sesji (zmiany nigdy niescommitowane) —
ten dokument to zapis stanu wiedzy, żeby następne podejście nie zaczynało od zera.

## Kontekst

Provider `Llm:Provider=local` (OpenAI-compatible: Ollama/llama.cpp, ale też — do TESTÓW LOKALNYCH
NA ŻYWO, nie produkcji — Google Gemini/Gemma przez `https://generativelanguage.googleapis.com/v1beta/openai/`)
u modeli z „thinking" (np. `gemma-4-31b-it`) zwraca w strumieniu SSE surowe znaczniki
`<thought>…</thought>` wplecione w `delta.content`. To NIE dotyczy produkcyjnych providerów
(Claude, lokalny Bielik) — dotyczy wyłącznie eksperymentów z Gemini/Gemma opisanych w
[RUNBOOK-LLM-PROVIDER.md](RUNBOOK-LLM-PROVIDER.md).

## Problem

Bez wydzielenia, tekst myślenia trafia 1:1 do widocznej odpowiedzi
(`OpenAiCompatibleLlmProvider.StreamCompletionAsync` → `TokenEvent` → `ChatService.full`), co psuje:

1. **Panel analizy dokumentów** (`/analiza`) — `AnalysisPrompts.ParseVerdict`
   ([AnalysisPrompts.cs:41-44](../src/PrawoRAG.Api/Services/AnalysisPrompts.cs)) czyta werdykt
   z PIERWSZEJ LINII odpowiedzi (`WERDYKT: OK/RYZYKO/BRAK ŹRÓDEŁ`). Gdy pierwsza linia to fragment
   `<thought>`, parsowanie pada → `UnitVerdict.Unknown` → badge „?" w UI zamiast realnego werdyktu.
2. Anty-fabrykację (`CitationValidator`) — dostaje do walidacji tekst z domieszką CoT.
3. Renderowany markdown w głównym czacie — użytkownik widzi surowe `<thought>` w odpowiedzi.

## Format odpowiedzi — ustalone empirycznie

**Non-streaming** (`stream:false`), pojedyncza odpowiedź:
```json
{"choices":[{"finish_reason":"stop","index":0,"message":{
  "content":"<thought>...tok myślenia...</thought>Tak, niebo jest niebieskie.",
  "extra_content":{"google":{"thought":true}},
  "role":"assistant"}}], ...}
```

**Streaming** (`stream:true`) — KAŻDA delta myślenia niesie osobną flagę
`delta.extra_content.google.thought = true`; delta, w której myślenie się kończy, TEJ FLAGI
już nie ma, ale wciąż zaczyna się od dosłownego tekstu `</thought>` sklejonego z właściwą
odpowiedzią (bez spacji/newline). Przykład (pełny zrzut w historii sesji 2026-07-23):
```
data: {"choices":[{"delta":{"content":"<thought>*   Question: ...","extra_content":{"google":{"thought":true}},"role":"assistant"}, ...}]}
...
data: {"choices":[{"delta":{"content":"</thought>Tak, niebo jest niebieskie...","role":"assistant"}, ...}]}
```
Czyli: flaga `extra_content.google.thought` jednoznacznie klasyfikuje deltę (thought/visible),
ale sam literalny tekst `<thought>`/`</thought>` to ARTEFAKT modelu doklejony na granicy —
trzeba go dodatkowo obciąć z treści (prefiks pierwszej delty myślenia / pierwszej delty odpowiedzi).

## Co było zrobione (i wycofane)

Provider-level fix w `OpenAiCompatibleLlmProvider`: routing delty po fladze
`extra_content.google.thought`, obcięcie literalnych tagów jako prefiksu, wydzielone „myślenie"
wystawiane przez nowy callback `LlmRequest.OnReasoning` (wzorem istniejącego `OnUsage` — wywoływany
raz, na końcu strumienia). W `ChatService` → nowy `ReasoningEvent`, w `Chat.razor` → akordeon pod
źródłami (analogiczny do `<details class="sources">`).

**Efekt na żywo: NIE ZADZIAŁAŁO.** Po restarcie apki: (a) badge „?" w `/analiza` wciąż się pojawiał
(czyli albo `<thought>` dalej leciał do `TokenEvent`, albo formatuje się inaczej niż w tekście
teście), (b) akordeon „Rozumowanie" w ogóle się nie renderował w UI. Przyczyna NIE ZOSTAŁA
zdiagnozowana — cofnięto zmianę zamiast dalej zgadywać (patrz [feedback: nie proponuj rozwiązań
bez zbadania realnych danych] — ta sama zasada dotyczy też "napraw i sprawdź", nie tylko progów).

## Otwarte pytania na następne podejście

1. **Czy proces faktycznie wystartował na nowym kodzie?** Restart nigdy nie został potwierdzony
   wprost (np. logiem startowym z timestampem buildu) — to NAJBARDZIEJ prawdopodobny podejrzany,
   ale niezweryfikowany.
2. **Realny prompt ≠ toy prompt.** Format SSE potwierdzony na krótkim pytaniu testowym
   („Czy niebo jest niebieskie?"). Prawdziwy prompt analizy dokumentu (ogromny, ze
   strukturalnymi regułami `GroundedPrompt`) w przykładzie z „fragmentu 5" pokazywał DODATKOWE
   artefakty nieopisane wyżej — `WERDYKT: RYZYKO` owinięte w osierocony blok ``` ``` ``` — sugeruje,
   że zachowanie modelu (i może kształt SSE) różni się przy długich, ustrukturyzowanych promptach.
   Trzeba złapać SUROWY strumień z REALNEGO zapytania (nie toy), nie zgadywać.
3. **Brakuje narzędzia do zrzutu ODPOWIEDZI.** Mamy `PRAWORAG_DUMP_PROMPT` (zrzuca ŻĄDANIE do LLM),
   ale nie ma odpowiednika po stronie SUROWEGO strumienia SSE odpowiedzi — bez tego diagnoza to
   zgadywanie na ślepo. Warto dodać analogiczną flagę (np. `PRAWORAG_DUMP_RESPONSE`) zrzucającą
   surowe linie `data:` przed jakimkolwiek parsowaniem, ZANIM próbuje się znowu wydzielać thinking.

## Zakres / priorytet

Dotyczy WYŁĄCZNIE prowizorycznego providera Gemini/Gemma używanego do lokalnych testów na tej
maszynie — nie blokuje niczego produkcyjnego (Claude/Bielik nie mają tego problemu). Niski
priorytet, do podjęcia gdy ktoś będzie chciał kontynuować testy z tym providerem.
