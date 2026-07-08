# Kontekst rozmowy (follow-upy) — Krok 1: heurystyka bez LLM

## Problem

Każde pytanie w czacie było niezależne: retrieval i prompt budowane wyłącznie z bieżącego pytania.
Użytkownik po odpowiedzi „blisko, ale nie w sedno" nie mógł dopytać („a co z § 2?", „rozwiń") —
dopytanie embeduje się bezwartościowo (retrieval nie ma czego szukać), a LLM nie wie, o czym mowa.

Follow-up wymaga obsługi w DWÓCH miejscach: (1) **generacja** — LLM widzi poprzednie tury;
(2) **retrieval** — dopytanie trzeba wzbogacić kontekstem. Naiwne „dorzućmy historię do promptu"
wygląda, jakby działało, ale retrieval nadal strzela z surowego dopytania → model odpowiada z historii
bez świeżych źródeł → cicho łamie gwarancję ugruntowania (rdzeń produktu).

## Rozwiązanie (Krok 1 — deterministyczna heurystyka, bez dodatkowego wywołania LLM)

### Retrieval: dwa warianty, wybór ASYMETRYCZNY z marginesem
Przy follow-upie (`history.Count > 0`) retrieval liczony 2x, SEKWENCYJNIE (wspólny scoped DbContext
nie jest thread-safe):
- (a) samo nowe pytanie,
- (b) `FollowUpQuery.Contextualize` — 2 ostatnie poprzednie pytania użytkownika + bieżące, sklejone.

**Wybór (`FollowUpQuery.PickContextual`): kontekstowy wygrywa, chyba że surowy pobije go o margines**
(`Retrieval:FollowUpSignalMargin`, domyślnie 0.02). Symetryczne „wygrywa wyższy sygnał" NIE działa —
zmierzone na M4: surowe „a co z § 2?" miało cosine 0.879 do PRZYPADKOWYCH fragmentów (krótkie pytanie
bez treści ma wysokie cosine do wszystkiego), kontekstowe 0.879 do właściwego art. 367; różnica 8e-6
to szum, a ostre `>` wybierało gorszy wariant. Asymetria odzwierciedla koszty pomyłek: fałszywe SUROWE
= źródła-śmieci albo fałszywa odmowa (psuje funkcję); fałszywe KONTEKSTOWE = sklejony tekst i tak
zawiera całe nowe pytanie, do promptu idzie oryginał — degradacja łagodna. Sam mechanizm istnieje,
BO dopytanie nie niesie treści — porównanie łeb w łeb temu przeczyło.
**Bramka abstynencji liczona z WYBRANEGO wyniku.**

**Synergia za darmo:** sklejony tekst niesie cytaty z historii („art. 367 KPC") → retrieval
strukturalny (QU-3) i `TemporalAugmenter` (nowele, AKT-2) działają na follow-upach bez zmian
w nich samych. Augmenter dostaje EFEKTYWNE (wybrane) zapytanie.

### Generacja: historia w prompcie
`GroundedPrompt.Build(question, chunks, history)` — ostatnie 4 tury jako naprzemienne User/Assistant
PRZED finalną wiadomością (PYTANIE+ŹRÓDŁA jak dotąd; oryginalne pytanie, nie sklejone). Zasada 7
w system promptcie: historia służy WYŁĄCZNIE zrozumieniu kontekstu; tezy tylko ze ŹRÓDEŁ bieżącej tury.

**Sanityzacja odpowiedzi historycznych (krytyczne):** markery `[n]` ZDJĘTE (stare `[3]` wskazywało
źródła tamtej tury — skopiowane przez model przeszłoby walidację anty-fabrykacji, wskazując inne
źródło) + przycięcie do 1500 znaków.

**Scalanie kolejnych User:** tura z abstynencją (samo pytanie, bez odpowiedzi) skleja się z następną
wiadomością User — ścisła naprzemienność ról. Messages API dziś łączy takie tury samo, ale historycznie
zwracało 400 „roles must alternate", a szablony czatu lokalnych modeli (Bielik/llama.cpp) bywają
wrażliwe — scalanie po naszej stronie jest bezpieczne dla KAŻDEGO providera.

### Anty-fabrykacja: per tura (bez zmian)
Cytaty walidowane względem źródeł BIEŻĄCEJ tury. Model powtarzający „art. X" z historii nieobecny
w bieżących źródłach → flaga `suspicious` — to POPRAWNE (gwarancja ugruntowania zostaje per tura).

## Gdzie w kodzie

| Element | Plik |
|---|---|
| `ChatTurn(Question, Answer?)` | `src/PrawoRAG.Domain/Llm/ChatTurn.cs` |
| `FollowUpQuery.Contextualize` | `src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs` |
| Prompt z historią + sanityzacja + scalanie | `src/PrawoRAG.Llm/Grounding/GroundedPrompt.cs` |
| Dual-retrieval (UI) | `src/PrawoRAG.Api/Services/ChatService.cs` |
| Parytet SSE (`ChatRequest.History`) | `src/PrawoRAG.Api/Program.cs` (`/api/chat`) |
| Historia z `_exchanges` (Done && !Error) | `src/PrawoRAG.Api/Components/Pages/Chat.razor` |
| Testy (czyste, bez DB/LLM) | `tests/.../FollowUpQueryTests`, `GroundedPromptHistoryTests`, `ChatServiceFollowUpTests` |

Koszt: +1 embedding +1 zapytanie SQL na turę follow-up (~200-400 ms); pierwsza tura bez zmian
(1 retrieval, prompt bez historii — zero regresji jednoturowej).

## Weryfikacja na M4 (z korpusem)

1. UI: „co mówi art. 367 Kodeksu postępowania cywilnego?" → odpowiedź; potem „a co z § 2?" →
   źródła zawierają art. 367 KPC, odpowiedź nawiązuje do poprzedniej tury, „✓ cytaty zgodne".
2. Parytet SSE:
   ```bash
   curl -s localhost:5024/api/chat -N -H 'content-type: application/json' \
     -d '{"question":"a co z § 2?","history":[{"question":"co mówi art. 367 KPC?","answer":"Art. 367 stanowi..."}]}'
   ```
3. Zmiana tematu po turach o KPC („jaka kara grozi za zabójstwo?") → źródła KK, bez skażenia KPC.
4. Regresja jednoturowa: pierwsze pytanie → zachowanie identyczne jak dotąd.

## Poza zakresem (jawnie odłożone)

- **Krok 2 — kondensacja zapytania LLM-em** (trudne anafory: „a ten drugi wyrok?"): tani call LLM
  przepisuje dopytanie na samodzielne pytanie. Osobny plan: wybór modelu (lokalny Bielik = ryzyko
  jakości przepisania), prompt, fallback przy złej kondensacji.
- Wczytywanie zapisanych rozmów po restarcie obwodu Blazora (store zapisuje, UI nie odtwarza).
- Kategoria multi-turn w golden secie (eval pozostaje jednoturowy).
