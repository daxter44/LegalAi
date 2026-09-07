# Czy jesteśmy gotowi wgrać orzeczenia NSA/WSA od 2016 r.? — 2026-09-07

Pytanie: zanim wgramy większą porcję wyroków sądów administracyjnych, czy mamy dobre mechanizmy
oczyszczania danych (znaki w plikach, dzielenie na fragmenty, wycinanie powtarzalnych kawałków)?

Metoda: liczby z bazy produkcyjnej (192.168.100.11) na wyrokach NSA/WSA, które JUŻ są w korpusie,
plus przegląd kodu ingestii (`NsaNormalizer`, `TokenAwareChunker`, skrypt pobierania) i ostatnich
diagnoz jakości danych. Wszystkie liczby poniżej są zmierzone, nie szacowane.

## Co już mamy w korpusie

| | |
|---|---|
| wyroków NSA/WSA w bazie | 10 738 (wszystkie zaindeksowane) |
| fragmentów | 129 460 |
| lata | 1980–2026, próbka losowa (skrypt bierze pierwsze N wierszy zbioru, bez filtra dat) |
| z tego od 2015 r. | 5 830 wyroków |

Czyli to nie jest 303 wyroki ze smoke testu z lipca. Ktoś dograł około 10 tysięcy i one już
pracują w wyszukiwarce. Wnioski niżej bazują na tej próbce.

## 1. Znaki w plikach — CZYSTO

| problem | ile fragmentów | udział |
|---|---|---|
| zepsute kodowanie (`∏Ê˝ƒ` itp., plaga w starych PDF-ach ustaw) | 0 | 0% |
| glif „⚫” (plaga w SAOS, 35 tys. fragmentów) | 0 | 0% |
| kratki formularza „☐ ☒” (druk uzasadnienia w SAOS) | 0 | 0% |
| sklejone zdania bez spacji („czasu.Nadto”) | 87 | 0,07% |
| tokeny anonimizacji „[...]” | 54 515 | 42% |

Tekst z JuDDGES jest płaski i czysty. Trzy problemy, które kosztowały nas tygodnie przy ustawach
i SAOS, tu nie występują. Sklejone zdania to margines, w większości skróty typu „Przegl.Podat.”.
Anonimizacja „[...]” jest cechą źródła (CBOSA tak publikuje), nie da się jej cofnąć i nie szkodzi
wyszukiwaniu ponad to, że zjada trochę tokenów.

## 2. Dzielenie na fragmenty — DZIAŁA, z dwoma uwagami

Normalizer dzieli tekst na dwie sekcje po słowie „UZASADNIENIE”: przed nim sentencja, po nim
uzasadnienie. Fragmenty uzasadnienia mają średnio 359 tokenów (limit 512), sentencji 140.
Fragmenty cieńsze niż 40 tokenów: 588 (0,5%) — filtr minimalnej treści działa.

**Uwaga A: 20% wyroków nie ma uzasadnienia.** 2 159 z 10 738 to sama sentencja (jeden fragment,
średnio 109 tokenów). W latach 2020–2024 to aż 730 z 2 849 (26%). To wyroki nieprawomocne,
do których uzasadnienie w CBOSA dochodzi później, a zbiór złapał je wcześniej. Sama sentencja
(„oddala skargę w całości”) nie odpowie na żadne pytanie prawne, a zajmuje miejsce w wynikach.
Decyzja do podjęcia: wgrywać i tak (i liczyć na delta-aktualizację, której jeszcze nie mamy),
czy pominąć wyroki bez uzasadnienia. Rekomendacja: pominąć na tym etapie, dopisać do listy
„do dogrania” gdy pojawi się mechanizm aktualizacji.

**Uwaga B: stare wyroki zaczynają się od „TEZY”.** 289 wyroków (głównie sprzed 2010) ma na
początku tezy, czyli krótkie streszczenie stanowiska sądu. To najcenniejsza część, ale wpada do
sekcji „sentencja” razem ze składem sądu. Dla wyroków od 2016 r. to 25 sztuk, więc dla planowanego
zakresu nieistotne. Kolejność sekcji jest zawsze sentencja przed uzasadnieniem (0 przypadków
odwrotnych), więc dzielenie po jednym słowie jest bezpieczne.

## 3. Powtarzalne fragmenty — TRZY RODZAJE, RÓŻNEJ WAGI

**Skład sądu w sentencji (76–78% sentencji).** Każda sentencja zaczyna się od:
„Wojewódzki Sąd Administracyjny w Poznaniu w składzie następującym: Przewodniczący Sędzia WSA …,
Sędziowie …, Protokolant …, po rozpoznaniu w dniu … sprawy ze skargi A. P. na postanowienie …
w przedmiocie odmowy wydania zaświadczenia oddala skargę w całości.” Nazwiska sędziów i protokolanta
to szum, ale ten sam fragment niesie przedmiot sprawy i rozstrzygnięcie, czyli treść cenną.
Normalizer SAOS wycina druk formularza; normalizer NSA nie wycina nic. Rekomendacja: usuwać
kawałek od „w składzie następującym” do „po rozpoznaniu” włącznie z nazwiskami, zostawiać resztę.
Tanie, deterministyczne, testowalne na obecnych 8 581 sentencjach.

**Stopki.** „Orzeczenie dostępne jest w Centralnej Bazie Orzeczeń Sądów Administracyjnych pod
adresem http://orzeczenia.nsa.gov.pl” kończy część wyroków. Czysty balast, do wycięcia.
Zdania o kosztach zastępstwa procesowego, choć powtarzalne (34 razy ta sama końcówka), są treścią
orzeczenia i zostają.

**Skopiowane akapity doktryny.** 545 tekstów fragmentów występuje w więcej niż jednym wyroku
(np. definicja przewlekłości postępowania powtórzona w 7–8 wyrokach słowo w słowo). To normalna
praktyka sądów, nie śmieć. Skutek uboczny: te same zdania mogą zająć kilka z ośmiu miejsc
w wynikach. Nie blokuje wgrania; ewentualne łączenie identycznych tekstów w wynikach to osobny,
mały temat po stronie wyszukiwarki.

## 4. Czego brakuje w narzędziach

1. **Skrypt pobierania nie ma filtra dat.** Ma tylko `--limit` i filtr „wyrok”. Żeby wgrać
   „od 2016”, trzeba dodać `--since 2016-01-01` (kilka linii w Pythonie). Bez tego skrypt bierze
   wyroki losowo ze wszystkich lat, tak jak przy obecnych 10 tysiącach.
2. **Brak dwóch wycinek** z punktu 3 (skład sądu, stopka CBOSA).
3. **Opcjonalnie: pomijanie wyroków bez uzasadnienia** (uwaga A) — filtr w normalizerze albo
   w skrypcie.
4. Nagłówek „sąd — sygnatura” jest ustawiany na sekcjach, ale chunker go nigdzie nie wstawia do
   tekstu fragmentu. Wyszukiwanie po sygnaturze działa osobnym torem po metadanych, więc to nie
   szkodzi; odnotowuję, bo komentarze w kodzie sugerują inaczej.

## 5. Ryzyko większe niż czystość danych: orzeczenia zalewają przepisy

Dwa pomiary z ostatnich dni mówią to samo. Dla klauzul umów wyszukiwarka zwracała niemal wyłącznie
orzeczenia sądów rejonowych, a właściwa ustawa wchodziła do wyników w 8 z 17 przypadków dopiero
po naprawie zapytania. Dla pytania o skargę na jednostkę budżetową właściwe rozporządzenie nie
weszło do pierwszych 50 wyników, bo miejsce zajęły orzeczenia. Dołożenie kilkuset tysięcy wyroków
NSA/WSA zwiększy tę konkurencję. To nie powód, żeby nie wgrywać (to jest treść, po którą prawnik
przychodzi), ale powód, żeby zmierzyć przed i po.

Skala: od 2016 r. to szacunkowo 250–350 tys. wyroków (lexedit ma 648 tys. ze wszystkich lat),
czyli 3–4 mln fragmentów i około 8–10 GB indeksu. Razem z obecnymi 18 GB zbliżamy się do
32 GB pamięci maszyny. Czas odpowiedzi po wgraniu trzeba sprawdzić, a nie założyć.

## Werdykt

**Czystość danych: gotowe.** Tekst z JuDDGES nie ma żadnego z problemów znakowych, które męczyły
nas przy ustawach i SAOS. Dzielenie na fragmenty działa poprawnie.

**Przed pełnym wgraniem trzy małe rzeczy i jeden pomiar:**

1. Filtr dat w skrypcie pobierania.
2. Wycinka składu sądu i stopki CBOSA w normalizerze (z testami na obecnych danych).
3. Decyzja o wyrokach bez uzasadnienia (rekomendacja: pominąć).
4. Pomiar przed i po na pilocie 20–30 tys. wyroków od 2016 r.: zestaw testowy czatu
   (`--chat`, dziś 50 z 53) i trafienie normy w analizie dokumentów (dziś 8 z 17). Jeśli
   przepisy zaczną wypadać z wyników, zatrzymać się i najpierw wzmocnić tor przepisów, dopiero
   potem dogrywać resztę.

Bez punktu 4 dowiemy się o pogorszeniu z diagnozy po skardze użytkownika, jak przy poprzednich
przypadkach.
