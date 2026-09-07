# Diagnoza: treść nowelizacji nieretrievalna dla pytań o datę wejścia w życie

Data: 2026-08-27. Punkt wyjścia: pytanie użytkownika „Jakie konkretne zmiany w ustawie Prawo
budowlane wejdą w życie we wrześniu 2026 roku?" — system poprawnie znalazł i zacytował klauzulę
wejścia w życie, ale odmówił podania treści zmian, mimo że ta treść jest w korpusie, w tym samym
akcie.

## Obserwacja (punkt wyjścia)

Odpowiedź systemu:

> Konkluzja: Nie można wskazać treści konkretnych zmian w ustawie Prawo budowlane, które wejdą
> w życie we wrześniu 2026 roku, ponieważ źródła podają jedynie termin wejścia w życie wybranych
> przepisów, nie określając ich merytorycznej zawartości [1].
>
> Uzasadnienie: Zgodnie z art. 13 ustawy z dnia 4 grudnia 2025 r. o zmianie ustawy - Prawo
> budowlane oraz niektórych innych ustaw, z dniem 20 września 2026 r. wchodzą w życie art. 1
> pkt 1 lit. a i c oraz pkt 3 [1]. Źródła nie obejmują treści tych przepisów.

To jest uczciwa odmowa (system nie zmyślił treści), ale przesłanka jest fałszywa: „źródła nie
obejmują treści" sugeruje lukę w korpusie. Nie ma luki.

## [FAKT — zmierzone na żywej bazie] Treść JEST w korpusie, w tym samym akcie

`DU/2025/1847` (Ustawa z dnia 4 grudnia 2025 r. o zmianie ustawy – Prawo budowlane…) ma w bazie
komplet chunków `ArticleNo='1'` — art. 1 to jedna wielka nowelizacyjna enumeracja („W ustawie…
wprowadza się następujące zmiany: …"), pocięta na 4 chunki po `ChunkIndex`. Chunk #2 zawiera
dosłownie treść „pkt 1 lit. c" (dodanie pkt 24–27 do słowniczka), chunk #3 zawiera „pkt 3" (zmiana
art. 9 ust. 3 pkt 3). Art. 13 (klauzula wejścia w życie, wskazująca dokładnie te podpunkty) też jest
w bazie, `ChunkIndex=34` tego samego dokumentu.

## [FAKT — zmierzone `--probe-chunk`] Wszystkie trzy chunki art. 1 są katastrofalnie nieretrievalne

Sonda (`Eval:ProbeEli=DU/2025/1847 Eval:ProbeArticle=1`) na dokładnym pytaniu użytkownika:

| chunk (fragment) | dokładny rank (fp32) | similarity | HNSW top-200 |
|---|---|---|---|
| wstęp art. 1 | **#2367** | 0,7870 | nieobecny |
| definicje (pkt 1 lit. c okolice) | **#50430** | 0,7516 | nieobecny |
| zmiana art. 9 (pkt 3) | **#82405** | 0,7443 | nieobecny |

Żaden z trzech nie zbliża się nawet do okna `CandidatesPerPath=50` używanego w produkcji — to nie
przypadek graniczny jak w diagnozie R7 (`POMIAR-PRAWO-UE-PO.md`, rangi #67/#88), to o **trzy rzędy
wielkości dalej**. HNSW (indeks aproksymacyjny) jest tu bez znaczenia — prawda (dokładny skan)
sama w sobie jest daleko, więc żadne dostrojenie `ef_search` czy podniesienie okna kandydatów tego
nie naprawi.

## Mechanizm (wniosek z pomiaru)

Pytanie niesie **ramę czasową** („co wejdzie w życie we wrześniu 2026") — to jedyny silny sygnał
semantyczny w zapytaniu. W całym dokumencie tylko **art. 13** (klauzula wejścia w życie) niesie
odpowiadający sygnał czasowy („z dniem 20 września 2026 r. wchodzą w życie…") — więc to on
monopolizuje wynik gęstego wyszukiwania. Sama treść nowelizacyjna (art. 1: definicje, zmiany
procedur) jest sucha, beztermowa prozą legislacyjną — zero nakładania się słownictwa z pytaniem o
datę. To nie usterka konkretnego chunka ani modelu — to **strukturalna właściwość** tej klasy
pytań: związek „art. 13 wskazuje art. 1 pkt X jako wyzwalacz" a „oto treść art. 1 pkt X" jest
**cytowaniem wewnątrz dokumentu**, nie podobieństwem semantycznym. Żaden ranking dense/BM25 tego
związku nie odda, bo nie ma go czego mierzyć w przestrzeni wektorowej.

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

Każda nowelizacja z rozłożonym w czasie wejściem w życie (częste w polskim prawie — długie
vacatio legis, przepisy przejściowe) ma dokładnie ten sam kształt: jeden artykuł „wchodzi w życie…
z wyjątkiem…" niosący WSZYSTKIE sygnały temporalne, i osobne artykuły z materialną treścią, zero
sygnału temporalnego. Pytania w stylu „co się zmieni od [data]" / „jakie przepisy wchodzą w życie
w [miesiąc/rok]" trafiają w ten sam strukturalny ślepy zaułek za każdym razem, niezależnie od aktu.

## Rekomendacja (nieoceniona kosztowo, do decyzji)

Ten sam wzorzec, który już działa w kodzie: **most cytowań** (`CitationBridgeAsync`,
`HybridRetriever.cs`) już dziś dociąga art. 415 KC, gdy trafione orzeczenie go cytuje — strukturalnie,
z pominięciem embeddingu. Naturalne rozszerzenie: **most vacatio legis** — gdy trafiony/exact-matched
chunk jest klauzulą wejścia w życie („ustawa wchodzi w życie… z wyjątkiem art. X pkt Y…"), sparsować
wskazane numery pkt/lit/artykułów **tego samego aktu** (już rozpoznawalne — to ten sam akt, więc
`ResolveActAsync` nie jest nawet potrzebny) i dociągnąć je strukturalnie, analogicznie do
`FetchArticleAsync`.

Nieoceniony jeszcze: (a) jak rozpoznać „to jest klauzula wejścia w życie" bez fałszywych trafień
(heurystyka na frazie „wchodzi w życie" + obecność odwołań pkt/lit?), (b) skala problemu — ile
pytań w `--refusals`/golden-secie faktycznie ma ten kształt (jedno zmierzone wystąpienie to n=1,
nie stopa błędu — ta sama zasada co przy innych diagnozach tej sesji).

## Rozwiązanie WDROŻONE (2026-08-27): most vacatio legis

Wariant A z rekomendacji, w kodzie: `VacatioLegis` (parser, `PrawoRAG.Domain/Retrieval`) +
`HybridRetriever.VacatioLegisAsync`. Pokrętło `Retrieval:VacatioLegisChunks` (domyślnie 8, `0` =
wyłączony i wynik bajt w bajt jak przed zmianą — ten sam idiom co `CitationBridgeArticles`).

Cztery decyzje, które wyszły dopiero przy implementacji, każda zamknięta testem:

1. **Dociągamy CAŁY wskazany artykuł, a numery pkt/lit służą do KOLEJNOŚCI.** Akt nowelizujący nie ma
   granulacji pkt/lit w lokalizatorze (art. 1 to jedna enumeracja pocięta po rozmiarze), więc adresowanie
   „pkt 3" jest niemożliwe — ale rankowanie po obecności znacznika w treści chunka działa, bo tekst
   „pkt 1 lit. c" fizycznie stoi w chunku #2, a „pkt 3" w #3.
2. **Parsujemy dopiero OD frazy „wchodzi/wchodzą w życie".** Chunk zaczyna się własnym nagłówkiem
   („Art. 13. Ustawa wchodzi w życie…"), więc parsowanie całości brało art. 13 za CEL i most dociągałby
   samą klauzulę, zjadając sloty przeznaczone na treść. Świadoma granica: rzadki szyk „art. 1 pkt 3
   wchodzi w życie z dniem…" (cel przed frazą) nie zostanie rozpoznany — wtedy most nic nie dokłada.
3. **Sama fraza „wchodzi w życie" nie włącza mostu** — warunkiem jest obecność wskazanych jednostek.
   Formuła końcowa („ustawa wchodzi w życie po upływie 14 dni") nie ma czego dociągać, a w korpusie
   stoi w każdej ustawie.
4. **Most omija próg `MinChunkTokens`** (jak tory exact-match, P5), bo chunki treści nowelizacji bywają
   krótkie — i na tym właśnie oparty jest deterministyczny test na żywej bazie.

Wpięcie: PO `Take(TopK)`, razem z rozszerzeniem sąsiedztwa. Dociągnięta treść nie konkuruje o sloty,
bo bez niej odpowiedź jest niemożliwa — system odmawiał, trzymając w ręku samą klauzulę.

Testy: `VacatioLegisTests` (10, parser — wzorcem jest DOSŁOWNA klauzula z tego przypadku) oraz
`VacatioLegisLiveTests` (4, żywy Postgres): treść wskazana w klauzuli wchodzi, artykuł niewskazany
NIE wchodzi, wyłączony most odtwarza pierwotną porażkę, limit chunków obowiązuje.

## Czego to NIE naprawia (wariant B, nierozstrzygnięty)

Most działa, gdy klauzula trafi do wyniku — tak było w tym przypadku. Nie pomoże, gdy klauzula nie
trafi (np. pytanie bez nazwy ustawy: „co zmienia się w budowlance od września"). Wariant B („znajdź
nowelizacje z datą wejścia w życie w oknie i dociągnij ich treść") wymaga danych, których nie ma
w kolumnie — i tu jest pułapka warta zapisania: **data wejścia w życie nie jest własnością dokumentu,
a pojedynczego przepisu** (art. 13 tego aktu rozkłada terminy na kilka grup przepisów). Jedna kolumna
„data wejścia w życie" byłaby więc nieprawdą dokładnie dla tej klasy nowelizacji. B zrobione uczciwie
to wyciąganie par (przepis → data) przy ingestii, nie backfill jednej kolumny.

Warunek wejścia w B: sonda na kilku parafrazach pytania (bez nazwy ustawy, sama data, sama dziedzina)
— czy klauzula w ogóle trafia do kandydatów. Dopóki trafia, most wystarcza.

## Narzędzie

`--probe-chunk` (`PrawoRAG.Eval/ChunkProbe.cs`), tylko odczyt, zero zmian w kodzie czy danych.
