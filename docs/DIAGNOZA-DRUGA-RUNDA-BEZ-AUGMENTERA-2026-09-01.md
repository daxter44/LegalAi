# Diagnoza: druga runda retrievalu (gap-closing) pomija TemporalAugmenter + UI nie czyści tekstu przy retry

Data: 2026-09-01. Punkt wyjścia: powtórny test pytania-nośnika z poprzedniej diagnozy
(`DIAGNOZA-DZIALALNOSC-NIEREJESTROWANA-BRAK-NOWELI-W-METADANYCH-2026-09-01.md`) PO wdrożeniu
poprawki `5d39851` (relink z warunkiem vacatio legis, wykonany i zweryfikowany tego samego dnia).
Oczekiwano, że naprawa metadanych rozwiąże problem. **Test wypadł gorzej niż poprzednio**: ta sama
odmowa, ale poprzedzona bardzo długim rozumowaniem (41655 znaków) i myloną prezentacją w UI.

Właściciel zadał trzy pytania:
1. Dlaczego ustawa Prawo przedsiębiorców nie wpadła już w pierwszym pytaniu?
2. Dlaczego system pokazał "Nie znalazłem jednoznacznej podstawy prawnej..." mimo że tura się
   jeszcze nie skończyła (licznik "Piszę odpowiedź… 250 s" dalej szedł)?
3. Dlaczego sekcja po dopytaniu (druga runda) trwała tak długo?

## Namierzona konwersacja w bazie

```
user      2026-09-01 19:05:41 UTC  "Do jakich obrotów mogę prowadzić działalność nierejestrowaną ?"
assistant 2026-09-01 19:12:36 UTC  Content = "Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania."
                                   Abstained = true, 18 źródeł w RetrievedSources
```

Całkowity czas tury: **6 min 55 s**. Odpowiedź jest poprawnie sklasyfikowana jako prawdziwa odmowa
(zero cytowań [n] — mechanizm ODM-4 z dzisiejszego rana zadziałał tu bez zarzutu).

## [FAKT — zmierzone w bazie] Właściwy przepis JEST w finalnej puli — ale bez znacznika nowelizacji

18-elementowa pula `RetrievedSources` (identyczna co do zawartości i kolejności z testu sprzed
poprawki relinku) zawiera na pozycjach 15–16 dokładnie te same dwa źródła co poprzednio:

| # | Źródło | `AmendmentEffectiveDate` |
|---|---|---|
| 15 | `DU/2018/646` (Prawo przedsiębiorców), art. 5 — wersja 75%/miesiąc | **null** |
| 16 | `DU/2025/1168` (nowela z 25.07.2025), art. 5 — wersja 225%/kwartał | **null** |

To znaczy: **właściwy przepis TRAFIŁ do puli** (wbrew wrażeniu właściciela, że go tam nie było —
zrozumiałe, skoro w UI widoczne było tylko pierwsze rzucające się w oczy źródło, "Przepisy
wprowadzające... art. 24 § 1bb", pozycja dużo wcześniejsza na liście 18 elementów). Ale mimo że
dane w bazie SĄ już poprawione (`DU/2018/646.unabsorbedAmendments` zawiera `DU/2025/1168` od
`2026-09-01 12:43:02 UTC` — zweryfikowane bezpośrednio SQL-em, ponad 6 godzin przed tym testem),
**znacznik `[NOWELIZACJA — JUŻ OBOWIĄZUJE]` nie został doklejony do żadnego z tych dwóch źródeł**.

## Przyczyna 1 (Q3, serwer): druga runda retrievalu w ogóle NIE WOŁA `TemporalAugmenter`

```csharp
// ChatService.cs — augmenter.AugmentAsync wystąpuje w CAŁYM PLIKU dokładnie RAZ:
177:  try { chunks = await LatencyLog.TimeAsync("augment", () => augmenter.AugmentAsync(query, result.Chunks, ct)); }
```

To wywołanie jest częścią przetwarzania PIERWSZEJ rundy. Gałąź obsługująca odmowę treściową
i wywołującą drugą rundę (linie ok. 268–306) bierze wynik wprost z `GapClosingRetrieval.RetrieveAsync`
i przekazuje go do `GroundedPrompt.OrderForGrounding`/`Build` **bez przepuszczenia przez augmenter
ani razu**. Sprawdziłem też `GapClosingRetrieval.cs` — augmenter jest tam wyłącznie WSPOMNIANY
w komentarzu wyjaśniającym, dlaczego cytat w zapytaniu ma znaczenie; sam kod nigdy go nie wywołuje.

**Skutek**: mechanizm `TemporalAugmenter` (AKT-2/AKT-4b), zbudowany specjalnie po to, żeby LLM nie
musiał sam liczyć dat z surowego tekstu przypisu (`BuildMarker`, komentarz: *"LLM nie zna dzisiejszej
daty z kontekstu treningu — musi dostać gotowy werdykt, nie surowe dane do policzenia samemu"*),
**jest całkowicie wyłączony na ścieżce drugiej rundy** — dokładnie tam, gdzie jest najbardziej
potrzebny (pierwsza runda już zawiodła, model dostaje jeszcze jedną szansę bez żadnej dodatkowej
pomocy). Dzisiejsza poprawka metadanych (`5d39851`) naprawiła dane, ale nie mogła nic zmienić w tym
wyniku, bo mechanizm czytający te dane nigdy nie został wywołany dla tej rundy.

To najpewniej tłumaczy wydłużone rozumowanie z pytania 3: model dostał dokładnie ten sam,
nierozstrzygnięty tekst z dwiema wersjami przepisu i przypisami ISAP, co poprzednio — musiał sam
próbować to rozgryźć od zera, bez pomocy systemu, i tym razem (w przeciwieństwie do wcześniejszego
testu z osobnym, jawnym dopytaniem o art. 5) nie udało mu się dojść do pewnej odpowiedzi.

## Przyczyna 2 (Q2/Q3, UI): `RetryingRetrievalEvent` nie czyści `ex.Answer` ani `ex.Reasoning`

```csharp
// Chat.razor — obsługa zdarzeń streamu:
773:  case RegeneratingEvent: ex.Answer = ""; ex.Regenerated = true; break;   // POPRAWNIE czyści
774:  case RetryingRetrievalEvent rr: ex.RetriedQuery = rr.NewQuery; break;   // NIE czyści ex.Answer
777:  case TokenEvent t: ex.Answer += t.Text; break;
780:  case ReasoningDeltaEvent rd: ex.Reasoning += rd.Text; break;
```

`RegeneratingEvent` (poprawka odpowiedzi na TYCH SAMYCH źródłach, inny mechanizm) poprawnie zeruje
`ex.Answer` przed nową generacją. `RetryingRetrievalEvent` (druga runda na NOWYCH źródłach) tego nie
robi — ani dla `ex.Answer`, ani dla `ex.Reasoning`. Skutek: tekst i rozumowanie z rundy 2 **doklejają
się** do tego, co już wyświetliło się z rundy 1, zamiast go zastąpić.

To tłumaczy **oba pozostałe pytania naraz**:
- **Q2** — użytkownik zobaczył na żywo tekst odmowy z rundy 1 (to normalne, tokeny strumieniują się
  widocznie zanim serwer w ogóle zdecyduje, czy robić drugą rundę — samo w sobie NIE jest błędem),
  ale kiedy zaczęła się runda 2, ten tekst NIE zniknął z ekranu — więc licznik "Piszę odpowiedź…"
  dalej rósł, a stary tekst zostawał widoczny, sprawiając wrażenie sprzecznego stanu (system
  „skończył" i „nie skończył" jednocześnie).
- **Q3 (rozumowanie 41655 zn.)** — `ex.Reasoning` sumuje rozumowanie OBU rund bez rozdzielenia,
  więc widoczna liczba znaków zawyża realny czas/rozmiar pojedynczego wywołania modelu. Runda 2
  faktycznie mogła długo myśleć (patrz Przyczyna 1), ale wyświetlona liczba to suma obu rund, nie
  sama runda 2.
- **"ucięło odpowiedź i jej nie ma"** — finalna treść w bazie NIE jest pusta (`Content` = poprawna
  fraza odmowy, sam serwer buduje `answer` od nowa przy każdej rundzie, to potwierdzone). Wrażenie
  „nic nie ma" pochodzi z tego, że wyświetlany tekst w przeglądarce był w tym momencie skleiony
  z dwóch rund bez separatora — realnie prawdopodobnie wyglądał na uszkodzony/podwojony, nie pusty,
  ale mogło to wywołać dokładnie takie wrażenie.

## Odpowiedzi wprost na pytania właściciela

1. **Dlaczego ustawa nie wpadła w pierwszym pytaniu?** Nie da się tego bezpośrednio zweryfikować
   z bazy — baza przechowuje wyłącznie FINALNĄ (drugą) pulę tury, nie pulę pierwszej rundy osobno.
   Ale przeformułowane zapytanie widoczne w retry-note ("Jaki jest **miesięczny limit przychodu**
   z działalności **nieewidencjonowanej**...") używa słownictwa ustawy ("przychód"), którego
   oryginalne pytanie ("Do jakich **obrotów**...") nie zawiera — to silna poszlaka za tym samym,
   już zdiagnozowanym mechanizmem niedopasowania słów (`project_term_mismatch_retrieval_pattern`,
   ten sam co OKI). To NIE jest coś, co naprawiła dzisiejsza poprawka metadanych — inny problem,
   dalej otwarty.
2. **Dlaczego pokazało odmowę mimo trwającej tury?** Częściowo normalne (strumieniowanie na żywo
   przed decyzją o retry), częściowo błąd: stary tekst rundy 1 nigdy nie zostaje wyczyszczony przy
   starcie rundy 2 (Przyczyna 2).
3. **Dlaczego druga runda trwała tak długo?** Prawdopodobnie oba mechanizmy naraz: (a) `TemporalAugmenter`
   nigdy się nie uruchamia w drugiej rundzie, więc model musiał sam mozolić się nad ustaleniem, która
   wersja przepisu obowiązuje (Przyczyna 1); (b) widoczna liczba znaków rozumowania to suma obu rund,
   nie tylko drugiej (Przyczyna 2) — realny czas jest krótszy niż sugeruje wyświetlona liczba, ale
   wciąż długi, bo model naprawdę nie miał pomocy, którą powinien był dostać.

## Otwarte — dwie oddzielne, jasno zlokalizowane poprawki do rozważenia

- **Serwer**: dociągnąć `augmenter.AugmentAsync(...)` też na ścieżce drugiej rundy w `ChatService.cs`
  (po `GapClosingRetrieval.RetrieveAsync`, przed `GroundedPrompt.Build`) — analogicznie do pierwszej
  rundy. To bezpośrednio przywróciłoby działanie mechanizmu NOWELIZACJA dla odpowiedzi wymagających
  drugiej rundy, czyli dokładnie tam, gdzie jest najbardziej potrzebny.
- **UI**: `RetryingRetrievalEvent` powinien zerować `ex.Answer` i `ex.Reasoning` tak samo jak robi to
  `RegeneratingEvent`, żeby druga runda renderowała się czysto, bez sklejania z pozostałością rundy 1.
- Problem niedopasowania słów w pierwszej rundzie (pytanie 1) pozostaje osobnym, wcześniej już
  zdiagnozowanym, wciąż otwartym zagadnieniem — nie do naprawienia w tym samym miejscu co powyższe.

## Narzędzie

Bezpośrednie zapytania SQL do `messages.RetrievedSources` (porównanie z wcześniejszą diagnozą — ta
sama pula co do treści, wciąż `AmendmentEffectiveDate: null` mimo poprawionych danych) oraz czytanie
kodu `ChatService.cs`/`Chat.razor`/`GapClosingRetrieval.cs` w poszukiwaniu miejsca wywołania
augmentera i obsługi zdarzeń streamu — bez potrzeby żywej instancji API ani sondy.
