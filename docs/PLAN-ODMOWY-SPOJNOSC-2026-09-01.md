# Plan: spójność odmów — ścieżka bez retrievalu i odmowa treściowa (zadania ODM)

Data: 2026-09-01. Status: **ZREALIZOWANE tego samego dnia (oba klastry, ODM-1..6), testy 915/915.**
ODM-3 rozstrzygnięte wg rekomendacji: odmowa „to nie prawo" NIE liczy się do metryki odmów
(zdanie zastępcze celowo bez frazy odmowy; przypieczętowane testem kontraktowym).
Do przeklikania na żywo scenariusze z sekcji „Kolejność i zależności".
Źródła: przegląd fixa `7e6f9a4` (pusta odpowiedź spoza prawa) + przypadek złapany żywcem przez
właściciela (follow-up: odpowiedź z martwym [22] bez panelu źródeł, po przeładowaniu rozmowy ta
sama tura jako żółty banner odmowy z pełną treścią odpowiedzi w środku).

## Klaster A — pusta odpowiedź na ścieżce bez retrievalu (słabości fixa 7e6f9a4)

Fix trafnie zdiagnozował lukę (SmalltalkPrompt bez reguły dla pytania nie-prawnego) i dodał
regułę 6 + fallback w UI. Słabości wykonania:

- **ODM-1: gwarancja po stronie serwera, nie promptu.** W `ChatService.SmalltalkAsync` zliczać
  wyemitowane tokeny; gdy strumień skończył się pustką/samymi białymi znakami — wyemitować
  standardowe zdanie odmowy (stała obok `SmalltalkPrompt`, ta sama fraza co w regule 6) PRZED
  `DoneEvent`. Efekt: treść zapisuje się do bazy, działa w czacie, `/api/chat` i dopytaniach
  Analizy, historia follow-upów bez pustych tur asystenta. Reguła 6 zostaje pierwszą linią.
  Akceptacja: test — SequenceLlm zwracający pustkę → w zdarzeniach jest token z odmową, a NIE
  pusty content; wiadomość zapisana niepusta.
- **ODM-2: zawężenie fallbacku w Chat.razor.** Dziś gałąź `else if (ex.Done)` renderuje „Nie
  pomagam w tematach spoza prawa" dla KAŻDEJ zakończonej tury z pustą odpowiedzią — także na
  ścieżce ZE źródłami (czkawka providera przy pytaniu prawnym = fałszywy komunikat „to nie
  prawo" obok panelu źródeł; to samo przy błędnej klasyfikacji routera). Po ODM-1 gałąź staje
  się martwym kodem bezpieczeństwa, ale ma przestać kłamać: tekst „nie pomagam w tematach spoza
  prawa" tylko dla tury bez retrievalu (UI zna trasę — renderuje belkę „nie przeglądałem bazy");
  dla ścieżki ze źródłami neutralne „Odpowiedź nie zawiera treści — zadaj pytanie ponownie."
- **ODM-3 (decyzja właściciela):** czy odmowa „to nie prawo" (ścieżka bez retrievalu) ma się
  liczyć do metryki nadrzędnej odmów? Rekomendacja: NIE (out-of-scope, nie porażka retrievalu) —
  dziś też się nie liczy (brak markera), ale po ODM-1 zdanie jest deterministyczne, więc łatwo
  to świadomie przypieczętować (test + komentarz przy zapisie telemetrii).

## Klaster B — odmowa treściowa: martwe [n], znikający panel źródeł, żółty banner po reloadzie

Zmierzone na żywym przypadku. Mechanizm: model złamał regułę 3 (fraza odmowy + dalsza odpowiedź
z cytowaniami w jednej wiadomości), a system wzmacnia skutki:

1. `IsRefusal` (Chat.razor) klasyfikuje przez `Contains(marker)` całą wiadomość → panel źródeł
   schowany, choć tekst ma klikalne [n] → martwe linki;
2. wariant A telemetrii zapisuje taką turę z `Abstained=true` i content = PEŁNA odpowiedź;
3. `LoadConversation` traktuje `Abstained=true` jak odmowę bramki → po przeładowaniu ta sama
   tura renderuje się jako żółty banner z całą odpowiedzią. Ta sama tura wygląda inaczej na żywo
   i po reloadzie.

- **ODM-4: doprecyzowanie definicji odmowy treściowej.** Odmowa = fraza obecna ORAZ odpowiedź bez
  cytowań [n] (albo: praktycznie sama fraza). Odpowiedź MIESZANA → normalna odpowiedź: panel
  źródeł widoczny, [n] działa. Ta sama definicja w OBU miejscach: render (IsRefusal) i zapis
  telemetrii (wariant A) — dziś metryka odmów zawyża, licząc odpowiedzi mieszane jako odmowy.
  Uwaga na spójność z Eval (`LiveReportRunner.IsContentRefusal`, `RefusalEvalRunner`) — te same
  kryteria albo świadomie opisana różnica.
  Akceptacja: test UI/klasyfikacji — wiadomość „fraza + treść z [2]" NIE jest odmową; sama fraza
  (± doklejka bramki) JEST.
- **ODM-5: odczyt historii rozróżnia odmowę bramki od treściowej.** Dyskryminator już jest
  w danych: odmowa bramki nie ma źródeł. `LoadConversation`: banner tylko gdy
  `Abstained && Sources.Count == 0`; inaczej zwykły dymek odpowiedzi (+ zachowanie IsRefusal
  z ODM-4 dla ukrycia/pokazania panelu). Akceptacja: test odtworzenia rozmowy z trzema turami
  (normalna / bramka / treściowa-mieszana) — render zgodny z widokiem na żywo.
- **ODM-6 (tanie, opcjonalne):** wzmocnienie reguły 3 w GroundedPrompt — „frazy odmowy nie łącz
  z odpowiedzią ani cytowaniami" wprost. Tylko pierwsza linia; gwarancję dają ODM-4/5.

## Kolejność i zależności

ODM-4 przed ODM-5 (wspólna definicja), ODM-1 przed ODM-2 (fallback ma zostać martwym kodem).
Klastry A i B niezależne od siebie. Wszystko czysto softwarowe — bez migracji, bez dotykania
bazy produkcyjnej, bez re-embeddingu; bramka = komplet testów + przeklikanie scenariuszy
z tego dokumentu na żywo (pytanie spoza prawa w 1. turze; follow-up prowokujący odmowę mieszaną;
przeładowanie rozmowy).
