# Raport diagnostyczny — trzy realne odmowy czatu (2026-07-18)

Materiał do przekazania dalej. Trzy niezależne, realne pytania użytkownika zakończone „Nie mam
wystarczających źródeł, aby odpowiedzieć." Dane wyciągnięte bezpośrednio z tabeli `messages` na
produkcyjnej bazie (192.168.100.11), model `gemma4:26b-mlx`, reranker włączony (Bielik: `sdadas/polish-reranker-roberta-v3`).

## Zastrzeżenie metodologiczne

`MessageEntity.RetrievedSources` (jsonb) trzyma źródła korpusowe podane do generacji. **Fragmenty
załączonego dokumentu (DOC) NIE są persystowane** — `Chat.razor:408-409` zapisuje tylko `ex.Sources`
(`SourcesEvent`), `ex.DocFragments` (`DocSourcesEvent`) zostaje wyłącznie w stanie UI i ginie z sesją.
Dla przypadku z załącznikiem (case 2) nie da się więc retrospektywnie sprawdzić, jakie fragmenty umowy
faktycznie trafiły do modelu — tylko że korpusowa część promptu przeszła bramkę.

**Kluczowe odkrycie wstępne, wspólne dla wszystkich trzech przypadków:** `Abstained = false` w bazie,
mimo treści „Nie mam wystarczających źródeł". To znaczy: bramka progu podobieństwa (`AbstentionPolicy.
ShouldAbstain`, `MaxSimilarity`) **przepuściła** za każdym razem — źródła (8, 11, 17) zostały pobrane
i podane modelowi. To **sam model** (`GroundedPrompt.SystemPrompt`, reguła 3) ocenił dostarczone źródła
jako nietrafne i odmówił na poziomie treści. To odmienny mechanizm od diagnozy z 2026-07-17 (tam problem
był w tym, że norma w ogóle nie wchodziła do puli kandydatów — tu norma/przepis **wchodzi**, ale albo
jest rozcieńczona zanieczyszczeniem, albo model jej nie rozpoznaje jako wystarczającej).

---

## Case 1 — Dzierżawa nieruchomości w trwałym zarządzie (follow-up)

**Wątek** (3 tury, follow-up na follow-upie):
1. *„Jakie zgody są potrzebne do oddania w dzierżawę nieruchomości którą jednostka budżetowa dysponuje
   w ramach trwałego zarządu?"* → **odpowiedź dobra** (20 źródeł, cytuje [1], poprawnie referuje UGN
   art. 60 ust. 1 pkt 1).
2. *„Kto jest organem właściwym lub nadzorującym?"* → **odpowiedź dobra** (11 źródeł, poprawnie
   rozróżnia UGN vs ustawę o zasadach zarządzania mieniem państwowym, cytuje [2][3][5][6]).
3. *„kto to będzie w przypadku gdy nieruchomością dysponuje jednostka budżetowa która ma przekazaną
   nieruchomość od jednostki samorządu terytorialnego - gminy?"* → **ODMOWA** (11 źródeł podanych).

### Źródła podane przy odmowie (tura 3)
```
KPA art. 5 § 1, art. 5 § 2, art. 268a                          — definicje ogólne, NIE o mieniu/zarządzie
UGN art. 43                                                     — TRAFNE (trwały zarząd)
UGN art. 48                                                     — prawdopodobnie trafne (sąsiedni przepis)
Ustawa o związku metropolitalnym w woj. pomorskim, art. 43      — ZANIECZYSZCZENIE (patrz niżej)
Ustawa o związku metropolitalnym w woj. pomorskim, art. 48      — ZANIECZYSZCZENIE
Ustawa o związku metropolitalnym w woj. pomorskim, art. 7a      — ZANIECZYSZCZENIE
Sąd Okręgowy w Elblągu, I Ca 347/14 (2014)
III CZP 35/04 (2004) — zduplikowane (2×)
```

### Obserwacja
Trafna norma (UGN art. 43/48) **jest** w źródłach — ale rozcieńczona: (a) ogólnymi definicjami z KPA
niewnoszącymi nic do pytania, (b) artykułami o TYCH SAMYCH numerach (43, 48, 7a) z zupełnie niezwiązanej
ustawy o związku metropolitalnym w woj. pomorskim (2026). Pytanie użytkownika NIE zawiera cytatu „art.
43" — więc to nie tor strukturalny (QU-3) na treści pytania.

**Mechanizm — NIEROZSTRZYGNIĘTY, dwaj kandydaci (nie wiadomo który, wymaga sprawdzenia na 3060):**
1. *Most cytowań* (`CitationBridgeAsync`) mógłby dociągać artykuł po samym numerze z cytowań w
   trafionych orzeczeniach — ale `ResolveActAsync` mostu rozpoznaje akty po krótkich aliasach (kc, kpc,
   …); bespoke ustawa z 2026 r. o związku metropolitalnym prawdopodobnie TAKIEGO aliasu nie ma, więc
   most raczej nie powinien jej dociągnąć. Do potwierdzenia empirycznie.
2. *Zwykłe rozproszone dopasowanie gęste* — to szeroki akt administracyjny (związek metropolitalny
   dotyka wielu tematów: mienia, wspólnej obsługi, zadań publicznych), którego liczne artykuły mogą
   embedować się generycznie blisko wielu zapytań samorządowych. Ten mechanizm lepiej pasuje do wzorca
   z Case 3 (patrz niżej — tam ten sam akt pojawia się z ROZPROSZONYMI numerami 16c/12/15/64/81/18/12b/7a,
   nie z numerami odpowiadającymi trafnemu przepisowi, co przeczy dopasowaniu „po numerze z cytowania").

Nie da się dziś potwierdzić, który mechanizm to faktycznie jest bez wglądu w bazę na 3060 (które
orzeczenie/query dociągnęło ten akt). Sama OBSERWACJA — że ten konkretny akt jest powtarzalnym
„atraktorem" w dwóch niezwiązanych pytaniach — jest solidna i warta zbadania niezależnie od mechanizmu.

**Możliwa przyczyna niepowodzenia modelu:** nawet mając UGN art. 43 w kontekście, model mógł nie
rozpoznać w NIM konkretnego przepisu odpowiadającego na pytanie „gmina jako przekazujący" (subtelna
różnica: Skarb Państwa vs jednostka samorządu terytorialnego jako źródło trwałego zarządu — może wymagać
innego ustępu niż ten, który trafił do kontekstu jako chunk).

---

## Case 2 — Umowa o dzieło (załącznik PDF)

**Pytanie** (zadane 3×, w tym powtórka „jeszcze raz" tego samego dnia i ponownie 2h później —
deterministyczne, `Temperature=0`, za każdym razem identyczny wynik): *„Z perspektywy kontraktora czy ta
umowa wystarczająco chroni jego interes?"*

**Wynik:** ODMOWA za każdym razem, 8 źródeł korpusowych podanych (fragmenty umowy — nieznane, nie
persystowane, patrz zastrzeżenie metodologiczne wyżej).

### Źródła korpusowe (8, ostatnia próba)
```
Sąd Apelacyjny w Gdańsku, I ACa 910/15 (2016) — zduplikowane (2×)
Sąd Apelacyjny w Katowicach, I ACa 1476/22 (2025)
Sąd Okręgowy w Szczecinie, II Ca 1178/14 (2015)
Sąd Rejonowy dla Warszawy-Śródmieścia, VI C 1124/20 (2021)
Sąd Apelacyjny we Wrocławiu, I ACa 1135/15 (2015)
Sąd Rejonowy w Słupsku, VI GC 162/14 (2015)
Sąd Okręgowy w Lublinie, I C 22/13 (2014)
```
Same tytuły nie mówią wprost, czy to sprawy o umowę o dzieło — wymaga sprawdzenia treści.

### Obserwacja
Kod (`ChatService.cs:57-71`) potwierdza: fragmenty załącznika są wybierane PO bramce abstynencji,
niezależnie od jej wyniku (bramka sprawdza tylko korpus) — więc skoro `Abstained=false`, fragmenty umowy
BYŁY dobrane i przekazane do promptu razem z 8 źródłami korpusowymi. Model mimo to odmówił.

**Dwie hipotezy, nierozstrzygnięte bez reprodukcji z `PRAWORAG_DUMP_PROMPT=1`:**
1. Dobór fragmentów dokumentu (in-memory cosine, `DocumentContext.SelectFragments`) nie trafił w
   klauzule istotne dla pytania — pytanie jest **oceniające/analityczne** („czy chroni interes"), nie
   faktograficzne, więc może słabo embedować się względem konkretnych klauzul umowy (kar umownych,
   odpowiedzialności, odbioru dzieła) tak jak faktograficzne pytanie trafia w faktografię.
2. Struktura promptu (osobna sekcja dla ŹRÓDEŁ korpusowych vs ZAŁĄCZNIKA) myli model co do tego, że
   wolno mu odpowiadać na podstawie dokumentu — może stosować regułę 3 SystemPromptu do korpusu, ignorując
   że ma też realny tekst umowy.

**Do zrobienia:** odtworzyć z `PRAWORAG_DUMP_PROMPT=1`, zobaczyć dokładnie jakie fragmenty umowy (jeśli
jakiekolwiek) faktycznie trafiły do promptu.

---

## Case 3 — Centrum Usług Wspólnych (rozliczenia międzygminne)

**Pytanie:** *„Czy jest możliwe przekazania Centrum Usług Wspólnych zadania polegającego na dokonywaniu
rozliczeń międzygminnych?"* (zadane 2×, druga próba to powtórka po pustej odpowiedzi).

**Próba 1 (17:26:03) — PUSTA ODPOWIEDŹ, pusty `Model`.** Retrieval zdążył zapisać 17 źródeł, ale
generacja nigdy nie doszła do końca — wskazuje na wyjątek wyrzucony w trakcie/przed generacją (pusty
`Model` = `DoneEvent` nigdy nie nadszedł normalnie).

**NIEROZSTRZYGNIĘTE, czy to crash rerankera.** Wygląda podobnie do sygnatury `TeiReranker` 422
(„batch size >32", naprawione tego samego dnia w `c30f57d`) — ale dane przeczą temu jednoznacznie:
**próba 2, to samo zapytanie 5 minut później, z tymi samymi 17 źródłami (retrieval deterministyczny),
NIE crashuje** — zwraca normalną odmowę treściową. Crash 422 rerankera jest deterministyczny względem
liczby kandydatów; to samo zapytanie powinno crashować identycznie za drugim razem, jeśli przyczyną
byłby rozmiar batcha. To, że nie crashuje, jest dowodem PRZECIW hipotezie rerankera, nie za nią.
Alternatywna hipoteza, nieprzetestowana: `gemma4:26b-mlx` jest wolna (zmierzone ~82s na krótszym
prompcie w tej samej sesji) — pełny prompt RAG mógł przekroczyć timeout HTTP/klienta i zostać przerwany
w trakcie generacji, dając pustą treść i pusty `Model` bez udziału rerankera. Nie da się dziś ustalić,
czy reranker był w ogóle włączony o 17:26 (brak logu konfiguracji w danych, którymi dysponuję) — to
samo w sobie jest lukę do zamknięcia, nie fakt do założenia.

**Próba 2 (17:31:29) — ODMOWA**, 17 źródeł.

### Źródła (17, wspólne dla obu prób — retrieval deterministyczny)
```
Ustawa o samorządzie gminnym, art. 10c                          — TRAFNE (wspólna obsługa/CUW)
Ustawa zmieniająca usg, art. 10c, art. 8e, art. 21               — TRAFNE (nowelizacja wprowadzająca CUW)
Ustawa o związku metropolitalnym w woj. pomorskim: art. 16c, 12,
  15, 64, 81, 18, 12b, 7a (8 różnych artykułów!)                 — ZANIECZYSZCZENIE, jak w Case 1
Ustawa o realizowaniu usług SPOŁECZNYCH przez centrum usług
  społecznych, art. 15, art. 12                                  — MYLĄCE (CUS ≠ CUW, patrz niżej)
KIO 2544/12 (2012)
```

### Obserwacje — DWA odrębne wzorce zanieczyszczenia
1. **Pułapka nazewnicza CUS vs CUW.** „Centrum Usług Społecznych" (ustawa z 2019 r., zupełnie inna
   instytucja — usługi społeczne dla mieszkańców) i „Centrum Usług Wspólnych" (wspólna obsługa
   jednostek organizacyjnych JST, usg art. 10a-10c) to **różne, leksykalnie bliskie** pojęcia. To ten
   sam rodzaj pułapki co art. 149 vs art. 415 KC z wczorajszej diagnozy (podobieństwo powierzchniowe,
   różna treść merytoryczna) — tylko na poziomie NAZW INSTYTUCJI, nie numerów artykułów.
2. **Ten sam „atraktor" co w Case 1**: ustawa o związku metropolitalnym w woj. pomorskim (2026) —
   **8 różnych artykułów** w źródłach dla pytania, które nie ma NIC wspólnego z metropoliami. W
   połączeniu z Case 1 (3 artykuły tej samej ustawy, też niezwiązane tematycznie) to silny sygnał, że
   ten konkretny akt jest **systemowo nadreprezentowany** w retrievalu — wart osobnego zbadania: czy to
   przez most cytowań (dopasowanie po numerze artykułu bez potwierdzenia aktu), czy przez jakość
   embeddingu tego konkretnego, świeżo zaindeksowanego dokumentu (podobnie jak zdegenerowane wektory
   `REGULATION` z wcześniejszej diagnozy — może ten akt ma nietypowo „centralne"/ogólne wektory).

---

## Podsumowanie — trzy odrębne, nowe wnioski (żaden nie pokrywa się z diagnozą z 2026-07-17)

1. **Bramka progu ≠ jakość odpowiedzi.** We wszystkich trzech przypadkach retrieval „zaliczył" próg
   (`Abstained=false`), ale model i tak odmówił — sam próg podobieństwa nie gwarantuje, że dostarczone
   źródła są UŻYTECZNE, tylko że są wystarczająco „podobne". To osobny wymiar jakości od tego, co
   naprawialiśmy dotąd (recall vs precyzja/trafność merytoryczna dostarczonych źródeł).
2. **Nowy, powtarzalny „atraktor"**: ustawa o związku metropolitalnym w woj. pomorskim (2026) pojawia
   się z wieloma różnymi numerami artykułów w DWÓCH niezwiązanych tematycznie pytaniach. Wymaga
   zbadania czy to wina mostu cytowań (dopasowanie po numerze bez potwierdzenia aktu — ryzyko
   architektoniczne dodanej dziś funkcji) czy jakości/charakteru wektorów tego konkretnego dokumentu.
3. **Pułapka nazewnicza CUS/CUW** — instytucje o zbliżonych nazwach, różnej treści, myląco bliskie
   semantycznie/leksykalnie.
4. **Fragmenty dokumentu-załącznika nie są persystowane** — luka w obserwowalności, utrudnia
   retrospektywne debugowanie odpowiedzi z załącznikiem (Case 2 pozostaje częściowo nierozstrzygnięty
   z tego powodu).
5. **Pusta odpowiedź / crash na żywym ruchu produkcyjnym** (Case 3, próba 1) — realny wyjątek podczas
   generacji, nie tylko w testach syntetycznych. Przyczyna NIEROZSTRZYGNIĘTA: dane przeczą prostej
   hipotezie „to ten sam bug rerankera co naprawiliśmy w `c30f57d`" (identyczne zapytanie 5 min później
   nie crashuje ponownie) — patrz analiza w Case 3. Wymaga dalszego zbadania (logi z 17:26, czy reranker
   był wtedy włączony, czy to raczej timeout generacji Gemmy).

## Uzupełnienie (analiza kodu, 2026-07-18 wieczór) — atraktor to najpewniej `TemporalAugmenter`

Raport rozważał dwa mechanizmy (most cytowań vs rozproszone dopasowanie gęste) — analiza kodu wskazuje
TRZECI, który pasuje do wszystkich obserwacji naraz: **augmentacja nowelizacyjna (AKT-2)**.
`TemporalAugmenter` dla każdego aktu w wynikach bierze WSZYSTKIE chunki jego niewchłoniętych nowel
i dokłada te, których tekst WZMIANKUJE pytany numer artykułu (`\bart\. N\b`) — bez capu, ze
Score=MaxValue (czoło listy), POZA limitem TopK. Ustawa o związku metropolitalnym (2026) to świeża
ustawa z pakietem przepisów zmieniających — jeśli figuruje w `unabsorbedAmendments` UGN/usg/KPA,
jest skanowana przy każdym pytaniu trafiającym te akty.

Dopasowanie do dowodów raportu:
1. **11 i 17 źródeł przy TopK=8** — tylko augmenter dokłada poza TopK (most i QU są cięte w retrieverze);
   nadwyżka = dokładnie fragmenty metropolitalne + „ustawy zmieniającej usg".
2. **Case 1: metropolitalne art. 43/48 = numery trafionych UGN art. 43/48** — augmenter szukał wzmianek
   „art. 43"/„art. 48"; nagłówek „Art. 43." WŁASNEGO artykułu noweli też matchuje luźny regex.
3. **Case 3: rozproszone numery własne (16c, 12, 15…)** — raport uznał to za dowód przeciw dopasowaniu
   po numerze, ale w mechanice augmentera to oczekiwany wzorzec: numer WŁASNY chunka jest dowolny,
   liczy się WZMIANKA pytanych 10c/8e/21 w treści („o którym mowa w art. 10c ustawy o samorządzie
   gminnym…"). Osiem fragmentów = brak capu.

**Naprawa (commit z tą zmianą):** kontrakt AKT-2 to „fragmenty nowel DOTYCZĄCE pytanych artykułów" —
czyli ZMIENIAJĄCE przepis. `AmendmentDiffMatcher`: fragment kwalifikuje się tylko, gdy wzmianka numeru
współwystępuje z językiem diffu ZTP („otrzymuje brzmienie", „dodaje się", „uchyla się", „skreśla się",
„zastępuje się") — odsiewa odesłania i nagłówki własnych artykułów. Do tego capy: ≤2 chunki per nowela,
≤4 łącznie. Testy T-AKT-DIFF (9, w tym dosłowne wzorce z Case 1/3).

**Weryfikacja na M4/3060:**
1. `SELECT "Title", "TypedMetadata"->'unabsorbedAmendments' FROM documents WHERE "DocType"='act' AND
   "TypedMetadata"::text ILIKE '%metropolitaln%'` — potwierdzić, że ELI ustawy metropolitalnej figuruje
   jako nowela wielu aktów (dowód rozstrzygający mechanizm).
2. Powtórzyć pytania Case 1/3 — źródła bez fragmentów metropolitalnych (chyba że ta ustawa FAKTYCZNIE
   zmienia pytany artykuł — wtedy ma prawo być, w limicie 2).
3. Golden set kategoria Freshness (`fresh-kpc-nowela`) — realna nowela DU/2026/473 ma język diffu,
   musi nadal wchodzić (strażnik, że zaostrzenie nie wycięło legalnych nowel).

Zaostrzenie NIE adresuje: pułapki CUS/CUW (Case 3.1 — problem embeddingowy), doboru fragmentów
załącznika przy pytaniach oceniających (Case 2) ani pustej odpowiedzi (Case 3 próba 1) — te zostają
otwarte jak niżej.

## Otwarte pytania do dalszej analizy
- Czy „atraktor" (związek metropolitalny) to bug w moście cytowań, czy zwykłe rozproszone dopasowanie
  gęste (szeroki akt, generyczne embeddingi) — wymaga sprawdzenia na 3060 (który dokładnie
  mechanizm/orzeczenie dociągnęło te artykuły; wzorzec z Case 3 z rozproszonymi numerami artykułów
  raczej przemawia PRZECIW mostowi cytowań, ale to niepewne bez wglądu w dane).
- Co faktycznie spowodowało pustą odpowiedź w Case 3 próba 1 (17:26:03) — crash rerankera to jedna z
  kilku hipotez, osłabiona przez brak powtórki crasha przy identycznym zapytaniu 5 min później; wymaga
  logów serwera z tego okna czasowego i potwierdzenia, czy reranker był wtedy w ogóle włączony.
- Reprodukcja Case 2 z `PRAWORAG_DUMP_PROMPT=1`, żeby zobaczyć realne fragmenty umowy w promptcie.
- Czy warto rozróżniać w UI/logice „odmowa progu" (Abstained=true) od „odmowa treściowa modelu"
  (RefusalMarker w Content, Abstained=false) w samej bazie/telemetrii — dziś nieodróżnialne bez
  parsowania treści, co utrudniało tę analizę.
