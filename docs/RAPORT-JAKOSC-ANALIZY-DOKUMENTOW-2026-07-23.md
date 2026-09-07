# Raport: jakość analizy dokumentów (`/analiza`) na realnym dokumencie — 2026-07-23

Sesja `019f8e48`, dokument: postanowienie ws. odmowy zaświadczenia o zgodności z m.p.z.p.
(gmina Kobierzyce), 10 fragmentów, pytanie użytkownika: „Przeanalizuj to postanowienie i daj
znać, czy odmowa jest zasadna? Sprawdź, czy powołane w treści postanowienia orzeczenia faktycznie
potwierdzają stanowisko organu... Czy warto się odwołać?"

**Cel tego dokumentu**: zebrać dowody i hipotezy do przeglądu przez mocniejszy model — NIE
proponuje się tu gotowej naprawy, poza jednym już wcześniej zdiagnozowanym i udokumentowanym
problemem (link niżej). Rozróżnienie: **[FAKT — zmierzone]** vs **[HIPOTEZA — do zbadania]**.

Wynik: **5× BRAK ŹRÓDEŁ, 3× RYZYKO, 2× OK** (na 10 fragmentów). Zgłoszenie użytkownika: korpus
zawiera cały SAOS + ISAP (+ świeżo NSA), a mimo to połowa fragmentów bez pokrycia — w tym RYZYKO
w fragmentach 5 i 10 sprowadza się w istocie też do „nie znaleziono cytowanego orzecznictwa".

## [FAKT] Orzeczenia BYŁY w indeksie, gdy analiza się uruchomiła

Ta sesja wcześniej ustaliła i naprawiła braki w `NsaNormalizer` (commit `65e4757`) oraz wgrała
3 konkretne cytowane orzeczenia (`III SA/Po 154/26`, `II SA/Gd 971/10`, `I OSK 16/16`) z CBOSA.
Zmierzone przez dekodowanie znacznika czasu UUIDv7:

| zdarzenie | czas (UTC) |
|---|---|
| `III SA/Po 154/26` wgrane (INSERT do `documents`) | 09:16:51.776 |
| Sesja analizy `019f8e48` (dolna–górna granica z prefiksu UUID) | 09:21:51–09:22:56 |

**Wniosek: to NIE jest wyścig/nieaktualność danych.** Orzeczenie było w pełni zaindeksowane
(status `Indexed`, embedding obecny — zweryfikowane wcześniej SQL-em) na ~5 minut przed analizą.

**KOREKTA (po otrzymaniu panelu źródeł fragmentu 5 od użytkownika)**: pierwsza wersja tego
raportu błędnie twierdziła, że fragment 5 też dostał odpowiedź „nie znajduje się w źródłach” dla
`III SA/Po 154/26`. To nieprawda — panel źródeł fragmentu 5 pokazuje `[6][7][8]` jako TRZY
faktyczne chunki tego właśnie wyroku (`Wojewódzki Sąd Administracyjny w Poznaniu, III SA/Po
154/26, 2026-02-11`). Retrieval dla fragmentu 5 **zadziałał** — model poprawnie ocenił treść
wyroku jako niepotwierdzającą ogólnej reguły, którą przypisuje mu organ (opisuje konkretny stan
faktyczny „kontenerowego obiektu zapleczowego”, nie ogólną zasadę). To ugruntowana, poprawna
krytyka na realnie pobranej treści — nie awaria retrievalu.

Rzeczywisty problem „nie znaleziono cytowanego orzecznictwa” dotyczy WYŁĄCZNIE:
- **Fragment 3** — `II SA/Gd 971/10` i `I OSK 16/16` (oba potwierdzone jako obecne w bazie
  o tym samym czasie co `III SA/Po 154/26` — wgrane w tym samym przebiegu `process`).
- **Fragment 10** — `III SA/Kr 660/16` (NIE sprawdzone w tej sesji, czy w ogóle istnieje w
  JuDDGES/CBOSA — możliwe, że to po prostu jeszcze nie zostało pobrane, inaczej niż pozostałe).

**To ważniejsze ustalenie niż początkowo sądzono**: ten sam dokument (postanowienie gminy
Kobierzyce), ten sam czas, ten sam korpus — a retrieval znalazł `III SA/Po 154/26` dla fragmentu 5,
ale nie znalazł `II SA/Gd 971/10`/`I OSK 16/16` dla fragmentu 3. To NIE jest systemowa niemożność
(„NSA nigdy się nie znajdzie”) — to NIESTABILNOŚĆ zależna od semantycznego dopasowania konkretnego
zapytania fragmentu do treści wyroku. Fragment 5 opisuje dokładnie ten sam stan faktyczny co
`III SA/Po 154/26` („kontenerowy obiekt zapleczowy”) — bardzo wyrazisty, rzadki wzorzec
semantyczny, łatwy do trafienia mimo że NSA to promil korpusu. Fragment 3 formułuje ogólną tezę
prawną o obowiązku wydania zaświadczenia — mniej wyrazisty semantycznie, łatwo przegrywa z
ogromną masą podobnych fragmentów k.p.a. z SAOS. To WSPIERA, nie podważa, wcześniejszą diagnozę
rankingu (pomiar: pozycja 140 dla ogólnego zapytania) — po prostu pokazuje, że wynik zależy od
tego, jak bardzo wyróżniający się jest KONKRETNY fragment, a nie że problem jest jednolity.

## [FAKT] Przyczyna: ranking + brak ścieżki po sygnaturze — już zmierzone, już opisane

Pełna analiza w [PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md](PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md).
Streszczenie relewantne tu:

- `TopK = 8` (`src/PrawoRAG.Api/appsettings.json:51`).
- Dla pytania wprost o treść sprawy fragmentu 5, najlepszy chunk `III SA/Po 154/26` w ścieżce
  gęstej (dense/cosine) wypadał na **pozycji 140** (na 800 sprawdzonych kandydatów, ef_search=800).
  Rząd wielkości poza jakimkolwiek realistycznym `TopK`.
- Zapytanie o samą sygnaturę (`III SA/Po 154/26`) w ścieżce BM25 dawało **0 trafień** — sygnatura
  nigdy nie trafia do treści chunka (`ContextHeader` ustawiany przez normalizer, nigdy nie czytany
  przez `TokenAwareChunker`).
- Skala: 303 dokumenty NSA to 0,004% korpusu (7,4 mln+ chunków) — nawet trafna treściowo
  odpowiedź ginie w statystycznym szumie, dopóki NSA nie urośnie (pełny backfill ~650 tys. wyroków
  częściowo to złagodzi, ale problemu sygnatur nie rozwiąże — patrz dokument linkowany).

To NIE nowe ustalenie — ten raport tylko potwierdza je na żywym biegu `/analiza`, z realnym
zapytaniem użytkownika, a nie syntetycznym testem SQL.

## [HIPOTEZA — do zbadania] Czy „BRAK ŹRÓDEŁ” w pozostałych fragmentach to bug, czy poprawne zachowanie?

Rozbicie 5 przypadków `BRAK ŹRÓDEŁ` pokazuje DWIE różne kategorie, które warto rozdzielić:

**Kategoria A — fragment nie zawiera żadnego twierdzenia prawnego do zweryfikowania** (fragmenty
1, 4): to opis stanu faktycznego / urwane zdanie kontynuujące poprzedni fragment. Model poprawnie
odmawia oceny („fragment nie zawiera treści odmowy, uzasadnienia ani powołanych orzeczeń"). To
może być **poprawne, oczekiwane zachowanie** anty-fabrykacji, nie defekt retrievalu — ALE stawia
pytanie, czy dzielenie dokumentu na jednostki (`LegalUnitSplitter`) nie tnie zbyt drobno, produkując
fragmenty strukturalnie niemożliwe do oceny.

**Kategoria B — fragment odwołuje się do treści INNEGO dokumentu, którego korpus fizycznie nie
zawiera i prawdopodobnie NIGDY nie będzie zawierał**: fragmenty 6, 7, 9 dotyczą treści KONKRETNEGO
miejscowego planu zagospodarowania przestrzennego gminy Kobierzyce (uchwała Rady Gminy nr
VI/82/2024) — dokumentu hiperlokalnego, publikowanego w wojewódzkim dzienniku urzędowym, poza
zakresem SAOS/ISAP/NSA. Model poprawnie mówi „źródła nie zawierają treści tego planu" — bo
rzeczywiście nie zawierają i nie ma to nic wspólnego z jakością retrievalu, tylko z zakresem
korpusu (miejscowe plany zagospodarowania to tysiące gmin, każda ze swoim planem — nierealne do
zaingestowania w całości).

**Pytanie do rozstrzygnięcia**: czy to jest znany, zaakceptowany limit produktu (system ocenia
zgodność z PRAWEM KRAJOWYM i orzecznictwem, nie z aktami prawa MIEJSCOWEGO), czy oczekiwanie
użytkownika jest inne? Jeśli to pierwsze — `BRAK ŹRÓDEŁ` w kategorii B jest poprawne i może warto
to nazwać wprost w UI („ten fragment odwołuje się do aktu prawa miejscowego poza zakresem
systemu”), zamiast generycznego „brak źródeł” sugerującego defekt.

## [HIPOTEZA użytkownika — do zbadania] Brak „stanu faktycznego” per fragment

Obserwacja z fragmentu 3: model SAM zauważył problem — ocenia ogólną tezę prawną w oderwaniu od
konkretnego stanu faktycznego sprawy, bo `AnalysisPrompts.MapQuestion`
(`src/PrawoRAG.Api/Services/AnalysisPrompts.cs:19-30`) **[FAKT — zweryfikowany w kodzie]** buduje
prompt fazy map WYŁĄCZNIE z treści jednej jednostki (`unit.Text`) — bez treści innych jednostek,
bez żadnego podsumowania faktów sprawy ustalonych gdzie indziej w dokumencie. Każdy fragment jest
oceniany w kompletnej izolacji od reszty dokumentu.

**Propozycja użytkownika**: jeden dodatkowy przebieg LLM PRZED fazą map, ustalający „stan
faktyczny” sprawy (z całego dokumentu albo z pierwszych jednostek), doklejany potem do promptu
KAŻDEGO fragmentu w fazie map.

Do rozważenia przy ocenie tej propozycji:
- **Koszt**: jeden dodatkowy call LLM per dokument (nie per fragment) — relatywnie tani wobec
  ~10-40 wywołań fazy map, które i tak już są (`CostGuard` liczy „dokument to kilkanaście wywołań
  LLM, każde liczone” — komentarz w `AnalysisRunner.cs:116`).
- **Ryzyko anty-fabrykacji**: samo ustalenie „stanu faktycznego” to ekstrakcja z dokumentu
  użytkownika (fakty, nie prawo — jak `DocFragment` w zwykłym czacie), więc nie powinno
  wprowadzać halucynacji o ile ekstrakcja jest ściśle ograniczona do „co jest napisane w
  dokumencie”, bez wnioskowania prawnego. Trzeba pilnować, żeby ten dodatkowy krok NIE zaczął
  wprowadzać ocen prawnych, które potem „zanieczyszczają” niezależność ocen per-fragment (dziś
  każdy fragment jest oceniany osobno WŁAŚNIE po to, żeby błąd w jednym nie zaraził innych —
  patrz komentarz w `AnalysisRunner.cs:8-17`; wspólny „stan faktyczny” to celowe złagodzenie tej
  izolacji, więc wymaga świadomej decyzji, nie tylko implementacji).
- **Czy rozwiązałoby akurat fragmenty 1/4/6/7/9?** Fragmenty 6/7/9 (kategoria B wyżej) NIE — to
  brak konkretnego aktu prawa miejscowego, stanu faktycznego to nie naprawi. Fragmenty 1/4 może
  częściowo, ale to fragmenty, które i tak strukturalnie nie mają nic do oceny (same fakty, zero
  twierdzeń prawnych) — dodanie stanu faktycznego do PROMPTU nie stworzy twierdzenia prawnego do
  zweryfikowania, tam gdzie fragment żadnego nie zawiera.
- Realny zysk widoczny byłby raczej przy fragmentach **oceniających ogólną tezę prawną** (jak
  fragment 3), gdzie znajomość konkretnego stanu faktycznego pomogłaby ocenić, czy teza jest
  stosowana do właściwego stanu faktycznego, a nie w oderwaniu od niego.

## [HIPOTEZA użytkownika — do zbadania] Prompt systemowy streszczenia

Treść streszczenia z sesji (pełny tekst w załączniku niżej) wymienia ryzyka i braki per fragment,
ale **nie odpowiada wprost** na pytanie użytkownika „czy warto się odwołać?” — mimo że to pytanie
jest literalnie przekazywane do promptu streszczenia (`AnalysisPrompts.SummaryInput`:
`"Pytanie użytkownika: {userPrompt}\n\n..."`).

**[FAKT — zweryfikowany w kodzie]** `AnalysisPrompts.SummarySystemPrompt`
(`src/PrawoRAG.Api/Services/AnalysisPrompts.cs:67-75`) instruuje model:
> „Napisz zwięzłe streszczenie całości... wskaż najważniejsze ryzyka i fragmenty bez pokrycia w
> źródłach. Zasady bezwzględne: NIE dodawaj żadnych twierdzeń prawnych, przepisów, sygnatur ani
> ocen, których nie ma w dostarczonych wynikach.”

To jest **celowe ograniczenie anty-fabrykacyjne** (raport ma być mechanicznie złożony z już
zweryfikowanych werdyktów, LLM nie ma prawa dopisać nowej oceny prawnej) — nie oczywisty błąd.
Ale rodzi napięcie: użytkownik zadał wprost pytanie „czy warto się odwołać”, a system celowo
unika bezpośredniej odpowiedzi, zamiast tego wyliczając ryzyka per fragment i zostawiając syntezę
użytkownikowi. **Pytanie do rozstrzygnięcia**: czy „czy warto się odwołać” to legalny
meta-wniosek z już wygenerowanych werdyktów (więc dałoby się go bezpiecznie dodać bez łamania
zasady „zero nowych twierdzeń prawnych”), czy to z natury nowa ocena prawna, której zasada
słusznie zabrania? Nie oczywiste i wymaga świadomej decyzji projektowej, nie samej zmiany prompta.

## Załącznik: pełna transkrypcja sesji (dane źródłowe, wklejone przez użytkownika)

Pytanie: „Przeanalizuj to postanowienie i daj znać, czy odmowa jest zasadna? Sprawdź, czy powołane
w treści postanowienia orzeczenia faktycznie potwierdzają stanowisko organu, a jeśli nie, to
zacytuj najistotniejsze fragmenty. Czy warto się odwołać?” Sesja: `019f8e48`. (Panele źródeł [n]
nie zostały skopiowane przez użytkownika — poniżej sam tekst werdyktów i uzasadnień.)

### Streszczenie
Analiza wykazała następujące ryzyka: błędna interpretacja charakteru postępowania wyjaśniającego
i zbyt szerokie wnioski z orzecznictwa (fragment 5) oraz brak treści wyroku WSA w Krakowie, co
uniemożliwia weryfikację stanowiska organu (fragment 10). Ryzyko dotyczy również ogólnej tezy o
dopuszczalności odmowy wydania zaświadczenia (fragment 3).

Brak źródeł uniemożliwia ocenę w fragmentach 1, 4 i 6 (brak treści rozstrzygnięcia i judykatów),
fragmencie 7 (brak przepisów o linii zabudowy) oraz fragmencie 9 (brak treści miejscowego planu
zagospodarowania przestrzennego).

### fragment 1 — BRAK ŹRÓDEŁ
> W dniu 29 czerwca 2026 r. do Urzędu Gminy w Kobierzycach wpłynął wniosek Pana Łukasza Sudoła o
> wydanie „zaświadczenia o zgodności projektu przebudowy budynku gospodarczego z ustaleniami
> obowiązującego miejscowego planu zagospodarowania przestrzennego na działce 195/1 w miejscowości
> Domaslaw, gmina Kobierzyce.” Do wniosku dołączono kserokopię postanowienia Powiatowego
> Inspektora Nadzoru Budowlanego…

Werdykt modelu: „Analizowany fragment zawiera jedynie opis stanu faktycznego i nie obejmuje treści
odmowy, uzasadnienia ani powołanych orzeczeń, co uniemożliwia ocenę zasadności decyzji oraz
weryfikację cytatów. Źródła wskazują jedynie, że postanowienie o odmowie wydania zaświadczenia
jest aktem, na który służy zażalenie [6].”

### fragment 2 — OK
> …miejscowego planu zagospodarowania przestrzennego albo decyzji o warunkach zabudowy i
> zagospodarowania terenu. Tut. organ jest w posiadaniu postanowienia PINB nr 95/2026 z dnia 2
> lutego 2026 r., prostującego omyłkę pisarską w postanowieniu nr 146/2024 tj. wskazującego, iż
> zaświadczenie Wójta Gminy Kobierzyce ma dotyczyć zgodności wykonywanej budowy budynku, a nie
> tylko jego rozbudowy…

Werdykt modelu: „Fragment poprawnie przywołuje podstawę prawną wydawania zaświadczeń zgodnie z
art. 217 § 1 [1], § 2 pkt 2 [2] oraz § 3 [3] k.p.a. Źródła nie obejmują kwestii zasadności odmowy
(analizowany fragment nie zawiera rozstrzygnięcia), poprawności powołanych orzeczeń (fragment nie
cytuje żadnych wyroków) ani oceny zasadności odwołania. Dostarczone orzeczenia [7, 8] dotyczą
innego stanu faktycznego i nie są przywołane w analizowanym fragmencie.”

### fragment 3 — RYZYKO
> W przypadkach, o których mowa we wspomnianym art. 217 § 2 pkt 2 k.p.a., organ administracji
> publicznej obowiązany jest wydać zaświadczenie, gdy chodzi o potwierdzenie faktów albo stanu
> prawnego, wynikających z prowadzonej przez ten organ ewidencji, rejestrów bądź z innych danych
> znajdujących się w jego posiadaniu (art. 218 § 1 k.p.a.). Organ administracji publicznej, przed
> wydaniem zaświadczenia…

Werdykt modelu: „Ogólna teza organu, że odmowa wydania zaświadczenia jest dopuszczalna, gdy żądane
potwierdzenie nie wynika z ewidencji, rejestrów lub innych danych w posiadaniu organu [6] lub gdy
problematyka jest sporna [7], [8], jest zgodna z dostarczonymi źródłami. Jednakże powołane w
fragmencie orzeczenia (WSA w Gdańsku z 10.03.2011 r. oraz NSA z 8 listopada 2017 r.) **nie
znajdują się w źródłach**, co uniemożliwia weryfikację, czy faktycznie potwierdzają one
stanowisko organu. Źródła nie obejmują również stanu faktycznego sprawy oraz pełnej treści
rozstrzygnięcia (fragment jest urwany), przez co nie można ocenić, czy w tym konkretnym przypadku
odmowa jest zasadna i czy warto się odwołać.”

*(Uwaga: „WSA w Gdańsku z 10.03.2011 r.” = `II SA/Gd 971/10`, „NSA z 8 listopada 2017 r.” =
`I OSK 16/16` — OBA potwierdzone jako obecne w bazie w momencie tej analizy, patrz sekcja FAKT
wyżej.)*

### fragment 4 — BRAK ŹRÓDEŁ
> …zaświadczenia. Konieczność uzyskania takiego zaświadczenia wynika z postępowania
> legalizacyjnego, prowadzonego na podstawie art. 48b ust. 1 i ust. 2 ustawy z dnia 7 lipca 1994 r.
> Prawo budowlane (Dz. U. z. 2026 r. poz. 524 ze zm.), przez Powiatowego Inspektora Nadzoru
> Budowlanego w Powiecie Wrocławskim i obowiązku wynikającego z postanowienia nr 146/2024 z dnia
> 19 kwietnia 2024 r. (sprostowanego n…

Werdykt modelu: „Nie można ocenić zasadności odmowy, poprawności powołanych orzeczeń ani
zasadności odwołania, ponieważ analizowany fragment nie zawiera treści rozstrzygnięcia ani
żadnych judykatów. Źródła [4, 5] potwierdzają jedynie, że w postępowaniu legalizacyjnym na
podstawie art. 48b p.b. dopuszczalne jest nałożenie obowiązku przedłożenia zaświadczenia o
zgodności budowy z m.p.z.p. [4, 5].”

### fragment 5 — RYZYKO
> W tym miejscu zauważyć należy, iż wnioskowane zaświadczenie pozostaje w ścisłym związku z
> postępowaniem legalizacyjnym. Postępowanie zainicjowane wnioskiem o wydanie zaświadczenia w
> celu legalizacji powinno dotyczyć tego samego obiektu, co toczące się przed organem nadzoru
> budowlanego postępowanie legalizacyjne (zob. wyrok Wojewódzkiego Sądu Administracyjnego w
> Poznaniu z dnia 11 lutego 2026 r., s…

Werdykt modelu: „Fragment budzi zastrzeżenia prawne, ponieważ organ błędnie interpretuje charakter
postępowania wyjaśniającego oraz wyciąga zbyt szerokie wnioski z przywołanego orzecznictwa.
1. Błędna interpretacja przepisu: Organ twierdzi, że jest „zobowiązany” przeprowadzić postępowanie
wyjaśniające na podstawie art. 218 § 2 k.p.a., podczas gdy przepis ten wprost stanowi, że organ
przed wydaniem zaświadczenia „może przeprowadzić w koniecznym zakresie postępowanie wyjaśniające”
[2]. 2. Niewłaściwe zastosowanie orzecznictwa: Powołany wyrok WSA w Poznaniu (sygn. akt III
SA/Po 154/26) **nie ustanawia** ogólnej reguły prawnej o konieczności tożsamości obiektu w obu
postępowaniach, lecz opisuje konkretny stan faktyczny, w którym skarżący wnioskował o
zaświadczenie dla „kontenerowego obiektu zapleczowego o funkcji mieszkalnej” w celu jego
legalizacji [6], [7], [8]. Orzeczenie to nie zawiera treści potwierdzającej tezę organu, jakoby
wskazanie we wniosku „przebudowy” budynku gospodarczego i dołączenie projektu przebudowy „nie ma
znaczenia” [6], [7], [8]. 3. Luki w uzasadnieniu: Źródła nie zawierają informacji, które
pozwalałyby uznać, że w przypadku legalizacji organ może zignorować żądanie zaświadczenia
dotyczące „przebudowy” na rzecz „budowy” obiektu. Warto rozważyć odwołanie ze względu na błędne
uznanie uprawnienia organu (możliwości) za obowiązek [2] oraz oparcie rozstrzygnięcia na
orzeczeniu, które dotyczy innego typu obiektu (kontenera) i nie odnosi się do kwestii przebudowy
[6], [7], [8].”

*(ROZSTRZYGNIĘTE — użytkownik przesłał panel źródeł fragmentu 5: [6][7][8] to TRZY faktyczne
chunki `III SA/Po 154/26` z korpusu, cytowane dosłownie i poprawnie przypisane. Retrieval
zadziałał; ocena modelu jest ugruntowana w realnie pobranej treści wyroku, nie w domysłach ani
w treści analizowanego dokumentu. Zob. korektę w sekcji „[FAKT] Orzeczenia BYŁY w indeksie” wyżej —
to fragment 3, NIE fragment 5, ma faktyczny problem „nie znaleziono cytowanego orzecznictwa”.)*

### fragment 6 — BRAK ŹRÓDEŁ
> Na podstawie ustaleń miejscowego planu zagospodarowania przestrzennego wsi Domasław, położonego
> w obrębach: Domasław i Księginice zatwierdzonego Uchwałą Rady Gminy Kobierzyce nr VI/82/2024 z
> dnia 25 października 2024r. opublikowaną w Dzienniku Urzędowym Województwa Dolnośląskiego Poz.
> 5452 z dnia 6 listopada 2024 r. działka nr 195/1 obręb Domasław leży na terenie oznaczonym
> symbolem 5MW - zabudow…

Werdykt modelu: „Nie mam wystarczających źródeł, aby odpowiedzieć.”

### fragment 7 — BRAK ŹRÓDEŁ
> § 6 ust. 1 i 2, stwierdzić należy, iż nie mają one zastosowania w omawianym przypadku. W § 3
> ust. 1 pkt 2 przedstawiono definicję nieprzekraczalnej linii zabudowy, przez którą należy
> rozumieć linię ograniczającą obszar, na którym dopuszcza się wznoszenie budynków oraz
> określonych w ustaleniach planu rodzajów budowli nadziemnych, niebędącymi liniami przesyłowymi,
> sieciami uzbrojenia terenu oraz zwią…

Werdykt modelu: „Dostarczone źródła [1]-[8] nie zawierają przepisów dotyczących
„nieprzekraczalnej linii zabudowy” ani limitów jej przekroczenia (np. 1,30m, 1,0m, 0,8m czy 40%
powierzchni elewacji), co uniemożliwia ocenę zasadności odmowy. W analizowanym fragmencie nie
przywołano żadnych orzeczeń, zatem nie można zweryfikować, czy potwierdzają one stanowisko organu.
Źródła nie obejmują również kwestii zasadności wniesienia odwołania w opisanym stanie faktycznym.”

### fragment 8 — OK
> Definicja ta zawiera enumeratywne wyliczenie elementów architektonicznych budynku, które nie
> mogą pomniejszać odległości liczonej od linii rozgraniczającej do nieprzekraczalnej linii
> zabudowy o więcej niż wskazana wyżej odległość. Jednocześnie zaznaczono, iż nieprzekraczalna
> linia zabudowy obowiązuje nowe budynki oraz rozbudowy części budynków istniejących. Z
> postanowienia Powiatowego Inspektora Na…

Werdykt modelu: „Odmowa jest zasadna, ponieważ odbudowa istniejącego wcześniej obiektu jest
traktowana na gruncie przepisów Prawa budowlanego jak budowa [6]. Taka inwestycja musi być zgodna
z aktualnie obowiązującymi przepisami, w tym warunkami technicznymi, a fakt, że obiekt istniał
wcześniej w danej lokalizacji, nie ma znaczenia [6]. W świetle tych zasad uznanie rozbudowy i
nadbudowy budynku gospodarczego za budowę nowego obiektu, do którego stosuje się aktualne
ograniczenia linii zabudowy, znajduje potwierdzenie w orzecznictwie [6].”

### fragment 9 — BRAK ŹRÓDEŁ
> …przypadku. Przepisy te stanowią, że na terenach przeznaczonych pod zabudowę ustala się
> możliwość rozbudowy, przebudowy i nadbudowy istniejących budynków położonych pomiędzy
> wyznaczonymi nieprzekraczalnymi liniami zabudowy a liniami rozgraniczającymi tereny dróg, z
> zakazem zmniejszania odległości budynku od drogi (ust.1). Dla istniejącej w dniu wejścia w życie
> planu zabudowy • wskaźnikach i paramet…

Werdykt modelu: „Źródła nie pozwalają ocenić zasadności odmowy, ponieważ nie zawierają treści
miejscowego planu zagospodarowania przestrzennego, na którym oparto rozstrzygnięcie. Organ
zrealizował ogólną zasadę obowiązku oceny zgodności budowy z ustaleniami planu [4], [6], jednak
bez dostępu do treści tego planu nie można zweryfikować, czy rozróżnienie między budynkiem
„nowym” a „istniejącym” w tym konkretnym przypadku jest prawidłowe. W analizowanym fragmencie nie
powołano żadnych orzeczeń, a źródła nie obejmują kwestii zasadności wniesienia odwołania w
opisanym stanie faktycznym.”

### fragment 10 — RYZYKO
> Domasław z ustaleniami tego planu. W tej sytuacji zasadnym jest odmówienie Wnioskodawcy wydania
> zaświadczenia o żądanej we wniosku treści. Jak wynika z treści art. 219 k.p.a., jeżeli organ nie
> ma w ogóle możliwości wydania zaświadczenia lub nie ma możliwości wydania zaświadczenia o treści
> żądanej przez osobę ubiegającą się o nie, to wówczas jedynym sposobem zakończenia takiego
> postępowania jest wy…

Werdykt modelu: „Odmowa wydania zaświadczenia w drodze postanowienia jest zgodna z art. 219 k.p.a.
[1]. Jednakże źródła nie zawierają treści przywołanego wyroku WSA w Krakowie (III SA/Kr 660/16),
przez co nie można potwierdzić, czy orzeczenie to faktycznie wspiera stanowisko organu. Źródła nie
obejmują kwestii zasadności wniesienia odwołania.”

*(`III SA/Kr 660/16` — NIE sprawdzone w tej sesji, czy istnieje w JuDDGES/CBOSA. Kandydat do
tego samego celowanego fetchu co pozostałe trzy, jeśli użytkownik chce kontynuować weryfikację
tego dokumentu.)*

## [FAKT + analiza] Czy pełny korpus NSA (~650 tys.) rozwiąże problem fragmentu 3?

Pytanie zadane wprost po korekcie wyżej: skoro wzrost korpusu wspiera diagnozę rankingu, to czy
dogranie pełnego backfillu NSA sprawi, że przypadki jak fragment 3 „się wyrównają"?

**[FAKT — zweryfikowane w kodzie]** W `HybridRetriever.cs:190-244` istnieje już DOKŁADNIE
analogiczny mechanizm — „most cytowań” (`CitationBridgeAsync`, diagnoza z 2026-07-17): gdy
trafione orzeczenia cytują konkretny PRZEPIS (np. „art. 415 k.c.”), most parsuje te cytowania,
głosuje po niezależnych dokumentach (próg `BridgeMinDocVotes=2`) i dociąga treść przepisu przez
DOKŁADNE zapytanie po metadanych — nie przez semantic search. Komentarz w kodzie wprost uzasadnia
to: „przepis rządzący jest nieretrievalny (przegrywa podobieństwo z narracjami orzeczeń)... ale
trafione orzeczenia SAME cytują normę, na której się opierają — sąd zrobił mapowanie stan
faktyczny→przepis lepiej niż jakikolwiek embedding”.

**Problem fragmentu 3 jest strukturalnie TEN SAM problem, tylko o jeden poziom wyżej**:
orzeczenie→orzeczenie zamiast orzeczenie→przepis. Analogicznego mostu dla cytowań judykatury
**nie ma** — potwierdzone: `grep` po `CitationBridge`/`Bridge` w całym `src/` nie znajduje nic
poza mostem przepisów.

**Odpowiedź: NIE, samo dogranie korpusu tego nie naprawi — i częściowo może pogorszyć.**

- Wzrost wolumenu NSA pomaga tam, gdzie liczy się REPREZENTACJA TEMATYCZNA (więcej dokumentów
  administracyjnych = większa szansa, że jakikolwiek trafny fragment z tej domeny wejdzie w
  `TopK`). To pomogłoby np. fragmentowi 9 (ocena zgodności z planem miejscowym w ogóle, gdyby
  plan był w korpusie).
- Fragment 3 to NIE problem reprezentacji tematycznej — to problem **znalezienia JEDNEGO
  KONKRETNEGO dokumentu** wśród podobnych. Semantic search nie „koncentruje się” na właściwym
  dokumencie wraz ze wzrostem wolumenu podobnej treści w tej samej domenie — WIĘCEJ NSA to
  WIĘCEJ konkurentów o te same sloty `TopK` dla tego samego ogólnego zapytania k.p.a., nie więcej
  szans na trafienie w TEN wyrok. Możliwe, że subiektywnie się pogorszy.
- Dodatkowa komplikacja: cytat we fragmencie 3 to „WSA w Gdańsku z 10.03.2011 r. oraz NSA z 8
  listopada 2017 r.” — BEZ sygnatury. Nawet naprawiona ścieżka po sygnaturze (Wariant B z
  [PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md](PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md)) by tu nie
  pomogła — nie ma czego szukać dokładnym dopasowaniem tekstowym.

**Kierunek naprawy (do oceny, nie zaimplementowany)**: mechanizm równoległy do `CitationBridge`,
tylko dla cytowań orzeczenie→orzeczenie. Dwa warianty wejścia:
1. Cytat z sygnaturą → dokładne dopasowanie po `caseNumber` (to samo, czego brakuje ogólnie —
   Wariant B z dokumentu o sygnaturach, tu użyty punktowo zamiast generycznie).
2. Cytat WYŁĄCZNIE opisowy (sąd + data, bez sygnatury, jak fragment 3) → dopasowanie po
   `CourtType`/`court` + `JudgmentDate` (dokładna data + zgodność sądu to silny, tani filtr —
   `JudgmentDate` już jest kolumną indeksowaną w `documents`). Nie wymaga NLP-owego parsowania
   nazwisk stron ani innych kruchych heurystyk — sama data w połączeniu z sądem prawdopodobnie
   wystarczająco zawęża wynik (w danym dniu dany sąd wydaje ograniczoną liczbę wyroków).

To NIE jest to samo zadanie, co ranking dla treściowych zapytań — to zadanie WERYFIKACJI CYTATU
(czy przywołane orzeczenie faktycznie mówi to, co twierdzi autor pisma), z natury wymagające
dokładnego dotarcia do TEGO dokumentu, nie „czegoś podobnego”.

## Otwarte pytania do przeglądu

1. Czy naprawa retrievalu po sygnaturze (wariant B z dokumentu linkowanego) powinna wejść PRZED
   kontynuacją testów `/analiza`, skoro to ona odpowiada za 2 z 3 przypadków RYZYKO w tej sesji
   będące w istocie „nie znaleziono cytowanego orzeczenia”?
2. Czy kategoria B (`BRAK ŹRÓDEŁ` z powodu aktu prawa miejscowego poza zakresem korpusu) wymaga
   zmiany w UI/komunikacie, czy to zaakceptowany limit produktu?
3. Czy dodać krok „stan faktyczny” przed fazą map — i jeśli tak, jak ograniczyć go, by nie naruszał
   izolacji ocen per-fragment (patrz `AnalysisRunner.cs:8-17`)?
4. Czy streszczenie powinno wprost odpowiadać na pytanie użytkownika, czy to sprzeczne z zasadą
   „zero nowych twierdzeń prawnych”?
5. ~~Fragment 5 — czy [6][7][8] tam użyte to źródła korpusu, czy treść WEWNĄTRZ analizowanego
   dokumentu~~ **ROZSTRZYGNIĘTE**: to prawdziwe źródła korpusu, ocena ugruntowana. Zamiast tego:
   dlaczego retrieval znalazł `III SA/Po 154/26` dla fragmentu 5, ale nie znalazł
   `II SA/Gd 971/10`/`I OSK 16/16` dla fragmentu 3 — na tym samym dokumencie, w tym samym
   momencie, w tym samym korpusie? Czy to czysto semantyczna wyrazistość zapytania (fragment 5
   opisuje rzadki, konkretny stan faktyczny; fragment 3 formułuje ogólną tezę prawną, gubioną
   w masie podobnych fragmentów k.p.a.), czy jest w tym coś systematycznego, co dałoby się
   poprawić bez czekania na naprawę sygnatur (wariant B z dokumentu linkowanego)?
