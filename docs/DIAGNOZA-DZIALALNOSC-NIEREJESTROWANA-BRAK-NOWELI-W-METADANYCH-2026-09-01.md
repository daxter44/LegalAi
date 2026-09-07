# Diagnoza: brakujący wpis w `unabsorbedAmendments` — model dostał obie wersje przepisu bez znacznika, który jest aktualny

Data: 2026-09-01. Punkt wyjścia: pytanie "Do jakich obrotów mogę prowadzić działalność nierejestrowaną?"
dostało **pełną odmowę** ("Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania.", zero
cytowań — poprawnie sklasyfikowane jako prawdziwa odmowa przez dzisiejszą poprawkę ODM-4). Ten sam
użytkownik, w tej samej rozmowie, dopytał wprost "a Ustawa ... Prawo przedsiębiorców (głównie art. 5)?"
i dostał poprawną, w pełni ugruntowaną odpowiedź (225% płacy minimalnej kwartalnie, art. 5 ust. 1).

**To piąty, odrębny mechanizm w tej serii diagnoz** — i jedyny dotychczas, w którym **retrieval
zadziałał bez zarzutu**. Problem nie leży ani w rankingu, ani w chunkowaniu, ani w słownictwie —
leży w niekompletnych metadanych, które miały rozstrzygnąć za model, która z dwóch wersji przepisu
obowiązuje dziś.

## [FAKT — zmierzone w bazie] Właściwy artykuł trafił do puli DRUGIEJ rundy — i mimo to skończyło się odmową

**Sprostowanie po pytaniu właściciela** (baza przechowuje wyłącznie FINALNĄ pulę źródeł tury, nie
osobno pulę każdej rundy — pierwotne sformułowanie tej diagnozy nie odróżniało tego wystarczająco
jasno): w Turze 1 doszło do DWÓCH rund retrievalu (widoczny w transkrypcie retry: "🔁 Źródła
z pierwszego wyszukiwania nie domykały pytania — szukałem też: [...]"). 18-elementowa pula
zapisana w `messages.RetrievedSources` dla finalnej (odrzuconej) odpowiedzi to najpewniej pula
DRUGIEJ, przeformułowanej rundy — przeformułowane zapytanie ("Jaki jest **miesięczny limit
przychodu** z działalności **nieewidencjonowanej**...") używa słownictwa ustawy ("przychód"),
podczas gdy oryginalne pytanie użytkownika ("Do jakich **obrotów**...") używa słowa, którego ustawa
w ogóle nie zna — czyli **runda 1 najpewniej ucierpiała na klasycznym niedopasowaniu słów**
(ten sam mechanizm co OKI/zaświadczenie), a mechanizm drugiej rundy (naprawiony wczoraj w `7e7e64f`)
zadziałał poprawnie: przeformułowanie znalazło właściwy przepis.

Ale nawet z właściwym przepisem w ręku po rundzie 2 — `DU/2018/646` (Prawo przedsiębiorców), art. 5,
oraz `DU/2025/1168` (nowelizująca ustawa z 25 lipca 2025 r.), też art. 5 — model i tak w pełni
odmówił. To NIE jest więc (tym razem) problem samego retrievalu, tylko tego, co się stało PO
znalezieniu właściwego źródła: model miał obie wersje przepisu w kontekście i nie potrafił
rozstrzygnąć, która obowiązuje.

## Mechanizm — tekst jednolity sam w sobie pokazuje DWIE wersje przepisu z przypisami, ale system nie połączył tego z metadaną o nowelizacji

Chunk art. 5 w `DU/2018/646` (tekst jednolity Prawa przedsiębiorców) **zawiera obie wersje przepisu
naraz**, dokładnie tak jak publikuje je ISAP — ze zwykłymi cyframi przypisów, nie z opisowym
oznaczeniem:

```
1.2) Nie stanowi działalności gospodarczej działalność ... której przychód należny ... nie
przekracza w żadnym MIESIĄCU 75% kwoty minimalnego wynagrodzenia ...
1.3) Nie stanowi działalności gospodarczej działalność ... której przychód należny ... nie
przekracza w żadnym KWARTALE 225% kwoty minimalnego wynagrodzenia ...
...
2) W tym brzmieniu obowiązuje do wejścia w życie zmiany, o której mowa w odnośniku 3.
3) W brzmieniu ustalonym przez art. 5 pkt 1 ustawy z dnia 25 lipca 2025 r. [...]
   (Dz. U. poz. 1168); wejdzie w życie z dniem 1 stycznia 2026 r.
```

Sama treść **jest** jednoznaczna dla uważnego czytelnika, który zna dzisiejszą datę: skoro dziś jest
2026-09-01, a zmiana weszła w życie 2026-01-01, wersja z 225%/kwartał obowiązuje od 8 miesięcy.
Ale system ma osobny, **istniejący** mechanizm zbudowany właśnie po to, żeby model nie musiał sam
liczyć dat z surowego tekstu przypisu — `TemporalAugmenter` doklejający jawny znacznik
`[NOWELIZACJA — JUŻ OBOWIĄZUJE od {data}]` / `[NOWELIZACJA — WEJDZIE W ŻYCIE {data}]`
(reguła 6 promptu). Ten mechanizm **nie zadziałał** dla tego przypadku:

```sql
select "TypedMetadata"->'unabsorbedAmendments' from documents where "ExternalId"='DU/2018/646';
-- [{"EliId":"DU/2026/507","EffectiveDate":"2026-10-14"},
--  {"EliId":"DU/2025/1826","EffectiveDate":"2026-01-03"},
--  {"EliId":"DU/2025/1795","EffectiveDate":"2026-03-18"}]
```

Lista zawiera **trzy inne** nowelizacje tego aktu (widocznie dotyczące innych artykułów) — ale
**nie zawiera `DU/2025/1168`**, tej właśnie nowelizacji, która zmieniła art. 5. Bez wpisu na tej
liście `TemporalAugmenter` nie ma jak przypisać `AmendmentEffectiveDate` do żadnego z dwóch
zwróconych źródeł — oba pola w retrievalu wyszły `null`. Model dostał więc surowy tekst z dwiema
liczbami i przypisami bez żadnego jawnego rozstrzygnięcia, które ma zaufać per reguła 6 promptu —
i musiał sam próbować odczytać przypisy oraz zestawić datę z dzisiejszą, czego nie zrobił w Turze 1
(wolał odmówić), a zrobił (poprawnie, ale bez pomocy systemu) dopiero w Turze 2, gdy użytkownik
zawęził uwagę modelu wprost do art. 5.

## Dlaczego to nie jest (wyłącznie) ten sam mechanizm co poprzednie diagnozy

- Runda 1 prawdopodobnie ucierpiała na znanym już problemie niedopasowania słów ("obroty" vs
  "przychód") — ale to nie jest przedmiot tej diagnozy, bo mechanizm drugiej rundy poprawnie to
  naprawił, zgodnie z projektem.
- Runda 2 (ta, którą faktycznie widział model przy generowaniu finalnej, odrzuconej odpowiedzi) MIAŁA
  właściwy przepis w obu wersjach — to nie problem rankingu ani rozmycia chunka na tym etapie.
- Nie przestarzała treść nowelizacji: to NIE jest przypadek, w którym stary chunk wygrywa z nowym —
  oba trafiły RAZEM do rundy 2, model miał komplet informacji, tylko bez rozstrzygnięcia która wersja
  obowiązuje.
- To luka w **kompletności metadanych** `unabsorbedAmendments` — mechanizm istnieje, działa dla
  innych trzech nowelizacji tego samego aktu, ale nie objął tej jednej, akurat relevantnej dla
  zadanego pytania.

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

ISAP regularnie publikuje teksty jednolite z dwiema równoległymi wersjami przepisu (stara/nowa,
z przypisami "obowiązuje do..."/"wejdzie w życie...") dokładnie w okresie przejściowym wokół daty
wejścia w życie zmiany — a nawet miesiące po niej, zanim ISAP opublikuje świeższy tekst jednolity.
Każdy przepis w takim stanie jest kandydatem na tę samą lukę, jeśli proces łączący akt bazowy z jego
nowelizacjami (relink/`sync-eli`) z jakiegoś powodu pominął akurat tę jedną nowelę, mimo że złapał
inne, niepowiązane ze sobą nowelizacje tego samego aktu.

## Otwarte — nieoceniona jeszcze skala i przyczyna luki w linkowaniu

- **Skala nieznana** (n=1 zmierzony przypadek). Nie sprawdzałem, czy to systemowa luka w procesie
  łączącym akty z ich nowelizacjami, czy odosobniony błąd dla tego jednego powiązania
  `DU/2018/646` ↔ `DU/2025/1168`.
- **Przyczyna luki w linkowaniu nieustalona** — czy `DU/2025/1168` w ogóle nie został rozpoznany
  jako nowelizujący `DU/2018/646` (mimo że tytuł "o zmianie niektórych ustaw..." obejmuje wiele
  aktów, może parser rozpoznaje tylko część z nich), czy rozpoznanie zaszło, ale coś przerwało zapis
  do `unabsorbedAmendments` tej konkretnej pary.
- **Kierunek naprawy nieoceniony** — to praca do wykonania w procesie ingestii/relinku
  (`sync-eli`/`AmendmentRelinkRunner` — ten sam moduł, który wczoraj dostał `AbsorbedAmendments.cs`),
  nie w retrievalu ani w prompt. Zgodnie z zasadą nietykania pipeline'u w locie bez rozmyślnego
  planu — to do decyzji właściciela, nie do samodzielnej poprawki.

## Narzędzie

Bezpośrednie zapytania SQL do `messages.RetrievedSources` (żeby zobaczyć realną pulę źródeł Tury 1,
nie zgadywać z treści odpowiedzi) i do `chunks`/`documents.TypedMetadata` na produkcyjnej bazie —
bez potrzeby żywej instancji API ani sondy `--probe-chunk` (retrieval nie był tu winowajcą).
