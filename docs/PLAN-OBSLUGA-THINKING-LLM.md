# Obsługa „myślenia" (thinking/reasoning) providerów OpenAI-compatible

Data: 2026-07-23. **ZAIMPLEMENTOWANE (drugie podejście)** — poprzednia próba wycofana bez diagnozy;
tym razem parser jest CZYSTĄ, przetestowaną funkcją, a nie zgadywaniem na żywo. Sekcje „Co było
zrobione (wycofane)" i „Otwarte pytania" niżej to zapis pierwszego podejścia (kontekst historyczny).

## Rozwiązanie (2026-07-23)

- **`ReasoningSplitter`** (`src/PrawoRAG.Llm/ReasoningSplitter.cs`) — automat stanowy rozdzielający
  strumień na widoczne + rozumowanie. Obsługuje OBA warianty: (a) Google flaga
  `extra_content.google.thought` (autorytatywna: jej brak = koniec myślenia) + literalne tagi
  `<thought>` jako artefakt do odrzucenia; (b) self-hosted gołe `<think>`/`<thought>` (także tag
  rozcięty między deltami, bufor granicy). Brak flagi i tagów (Claude/Bielik) → pass-through, zero
  regresji. **7 testów** (`ReasoningSplitterTests`) — poprawność NIE zależy od żywego biegu.
- **Provider** (`OpenAiCompatibleLlmProvider`): parsuje flagę per delta, przepuszcza przez splitter,
  emituje TYLKO widoczne delty (rozumowanie poza strumieniem → naprawia werdykt `/analiza`,
  walidację cytatów i renderowany markdown), oddaje rozumowanie przez `LlmRequest.OnReasoning`
  (raz, na końcu — wzorem `OnUsage`). Dodano `PRAWORAG_DUMP_RESPONSE` — zrzut SUROWYCH linii `data:`
  przed parsowaniem (postulat pkt 3 niżej; do diagnozy realnego strumienia bez zgadywania).
- **UI**: `ReasoningEvent` (ChatService) → w `Chat.razor` rozwijana sekcja „🧠 Rozumowanie modelu"
  (jak panel źródeł; tylko in-memory, nie persystowane). Analiza dokumentów korzysta pośrednio:
  strip rozumowania w providerze naprawia parsowanie werdyktu (badge „?").

## Model czy orkiestracja? (odpowiedź na pytanie z 2026-07-23)

Treść rozumowania = własność MODELU (reasoning-tuned checkpoint). Ale flaga `google.thought` =
orkiestracja GOOGLE — **self-hosted Ollama/llama.cpp jej nie wystawi**, tylko gołe tagi `<think>`
(lub pole `reasoning_content`). Dlatego splitter wykrywa oba; po przełączeniu na lokalny reasoning-model
rozumowanie zadziała out-of-the-box PRZEZ TAGI (o ile checkpoint faktycznie „myśli"), bez zależności
od flagi Google. **Nie zweryfikowane na żywej Gemmie w tej sesji** (brak dostępu do stacku) — parser
pokryty testami; przy pierwszym realnym biegu użyj `PRAWORAG_DUMP_RESPONSE`, jeśli coś odbiega od formatu.

---

## (Historia) TODO pierwszego podejścia — WYCOFANE

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
