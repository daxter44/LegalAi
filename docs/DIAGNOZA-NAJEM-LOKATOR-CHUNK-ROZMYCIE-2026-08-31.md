# Diagnoza: długi, wielotematyczny artykuł przegrywa z krótkim, ale niewłaściwym — rozmycie chunku, nie niedopasowanie słów

Data: 2026-08-31. Punkt wyjścia: pytanie "Najemca nie płaci czynszu — czy mogę wypowiedzieć umowę?"
dostało odpowiedź opartą WYŁĄCZNIE na ogólnych regułach Kodeksu cywilnego (2 pełne okresy zwłoki),
bez wzmianki, że dla lokalu mieszkalnego wynajmowanego "lokatorowi" w rozumieniu ustawy o ochronie
praw lokatorów (2001) obowiązuje **inny, wyższy próg (3 pełne okresy)** i przepisy KC o wypowiedzeniu
z tych przyczyn są **wyłączone** (lex specialis). Użytkownik skorygował to w drugiej turze, powołując
się na art. 11 ust. 2 pkt 2 tej ustawy — druga odpowiedź była już poprawna.

**To NIE jest przypadek niedopasowania słownictwa** (jak `DIAGNOZA-TERMIN-ZASWIADCZENIE-OSWIADCZENIE`
czy `DIAGNOZA-ZNAK-WODNY-AI-ACT`) — oba warianty artykułu dzielą dokładnie te same słowa z pytaniem
("najemca", "wypowiedzieć umowę", "czynsz"). To przypadek **rozmycia embeddingu długiego,
wielotematycznego artykułu** przegrywającego z krótkim, wąskim, ale merytorycznie niewłaściwym
konkurentem z tego samego aktu.

## [FAKT — zmierzone sondą] Właściwy przepis istnieje, jest zaembedowany, ale przegrywa z sąsiadem

Ustawa o ochronie praw lokatorów, art. 11 (zawiera w ust. 1 i ust. 2 pkt 2 dokładnie to, o co pyta
użytkownik — próg "trzy pełne okresy płatności") jest podzielona na 4 chunki. Cały interesujący
fragment (ust. 1 + ust. 2 pkt 1-4) mieści się w JEDNYM 447-tokenowym chunku (indeks 29).

| Kandydat | Długość | exact fp32 | HNSW (ef=400) | Trafność merytoryczna |
|---|---|---|---|---|
| **art. 11** (właściwy — próg 3 okresów) | 447 tok | pozycja **#512** (sim=0,7729) | **NIEOBECNY** w top-200 | Na temat |
| **art. 19p** (ten, który wygrał) | 71 tok | pozycja **#76** (sim=0,7921) | **obecny**, #71 | Niezwiązany — dotyczy najmu instytucjonalnego z dojściem do własności, wypowiadanego przez NAJEMCĘ, nie przez właściciela z powodu zaległości |

Art. 19p brzmi: *"Najemca może wypowiedzieć umowę najmu instytucjonalnego z dojściem do własności
z zachowaniem sześciomiesięcznego terminu wypowiedzenia z ważnych przyczyn..."* — krótki, jednotematowy,
i leksykalnie niemal dosłownie pokrywa się z pytaniem ("najemca", "wypowiedzieć umowę"), mimo że
mówi o zupełnie innej instytucji (najemca wypowiadający, nie właściciel; zupełnie inny tryb najmu).

Obie miary (dystans dokładny I obecność w indeksie przybliżonym) wskazują ten sam kierunek: krótki,
skoncentrowany chunk wygrywa nie dlatego, że jest trafny, tylko dlatego, że jego embedding jest
"czystszy" — a art. 11 przegrywa nie dlatego, że model źle rozumie pytanie, tylko dlatego, że jego
embedding uśrednia sygnał z KILKU różnych, niepowiązanych przesłanek wypowiedzenia upchniętych w
jednym bloku tekstu.

## Mechanizm — chunking nie nadąża za długością i strukturą przepisu

Art. 11 ustawy o ochronie lokatorów ma **12 ustępów** obejmujących zupełnie różne, niezależne
przesłanki wypowiedzenia: niewłaściwe używanie lokalu (ust. 2 pkt 1), **zaległość czynszową**
(ust. 2 pkt 2), bezprawny podnajem (ust. 2 pkt 3), konieczność remontu (ust. 2 pkt 4), własne
potrzeby mieszkaniowe właściciela (ust. 3-7), ochrona osób starszych (ust. 12), i inne. Chunker
dzieli ten artykuł WYŁĄCZNIE wg budżetu tokenów, bez świadomości granic ustępów — efekt: jeden chunk
miesza cztery różne, niepowiązane przesłanki wypowiedzenia (ust. 2 pkt 1-4) w jednym wektorze.

Dla porównania: artykuły Kodeksu cywilnego, na których oparta była PIERWSZA (niepełna) odpowiedź
(art. 672, 687 — reguła ogólna, 2 okresy) są krótkie i jednotematowe z natury, więc token-budżetowy
chunker daje im czyste, wąskie chunki bez dodatkowego wysiłku. **Im bardziej rozbudowany i ochronny
przepis (więcej wyjątków, więcej przesłanek — cecha typowa dla ustaw szczególnych/ochronnych), tym
gorzej wypada w tym schemacie chunkowania** — dokładna odwrotność tego, czego by się chciało.

## Dlaczego druga tura zadziałała

Użytkownik w drugiej turze podał dokładny numer artykułu i ustępu ("Art. 11 ust. 2 pkt 2"), co
powinno było uruchomić tor strukturalny (dokładne dopasowanie po numerze artykułu + akcie). W
praktyce odpowiedź poszła inną drogą — źródło [7] w drugiej odpowiedzi to nie sam przepis, tylko
**orzeczenie** (Sąd Okręgowy w Poznaniu, XV Ca 1681/13), które w swoim uzasadnieniu **dosłownie
cytuje** regułę z art. 11 ust. 2 pkt 2 ("wypowiedzenie umowy przez właściciela może nastąpić tylko
z przyczyn określonych w art. 11 ust. 2-5... Oznacza to, że wyłączone jest zastosowa[nie KC]").
Odpowiedź wyszła poprawna, ale przez przypadek — zadziałało, bo akurat trafione orzeczenie
przytaczało treść przepisu, a nie dlatego, że retrieval trafił bezpośrednio w chunk 29 art. 11
(który w drugiej turze też nie wszedł do puli źródeł). Gdyby żadne pasujące orzeczenie nie
istniało lub nie zostało trafione, druga tura zawiodłaby identycznie jak pierwsza.

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

Ustawy ochronne (o ochronie praw lokatorów, o ochronie konkurencji i konsumentów, prawo pracy w
części o wypowiadaniu umów) mają charakterystyczną strukturę: jeden centralny artykuł z długą listą
wyjątków/przesłanek, bo to w naturze przepisu ochronnego — wylicza WSZYSTKIE sytuacje, w których
ochrona ustępuje. To dokładnie ten kształt przepisu, który ten schemat chunkowania traktuje najgorzej.
Każde pytanie o "czy mogę X" wobec strony chronionej ustawą szczególną (lokator, konsument,
pracownik) jest kandydatem na ten sam błąd: ogólna reguła kodeksowa (krótka, czysto zaembedowana)
wygra z regułą szczególną (długa, rozmyta), nawet gdy ta druga powinna mieć pierwszeństwo.

## Otwarte — nieoceniona jeszcze skala i kierunek naprawy

- **Skala nieznana** (n=1 zmierzony przypadek). Warto sprawdzić inne długie, wieloustępowe artykuły
  ustaw ochronnych pod kątem tego samego wzorca rozmycia, zanim ktokolwiek zdecyduje o priorytecie.
- **Kierunek naprawy nieoceniony**: (a) chunking świadomy granic ustępów dla długich artykułów
  (dzielić po numerowanych jednostkach, nie tylko po budżecie tokenów) — celuje w przyczynę, ale
  dotyka ingestii/normalizera, więc wymaga rozważnego podejścia zgodnie z zasadą nie zmieniać
  działającego pipeline'u w locie; (b) CandidatesPerPath=50→100 (już na liście priorytetów) —
  **prawdopodobnie NIE pomoże tu samo z siebie**: art. 11 jest nieobecny w HNSW top-200, więc
  podniesienie okna kandydatów z 50 do 100 nadal nie sięgnie pozycji, na której faktycznie siedzi
  (dopiero root cause: sam HNSW go nie widzi w rozsądnym zasięgu); (c) polegać na moście podobnym do
  `CitationBridgeAsync`/mostu vacatio legis — gdy trafi się ogólna reguła KC dla "najmu lokalu",
  sprawdzić czy istnieje akt szczególny (ustawa o ochronie lokatorów) i podciągnąć jego przepis o
  wypowiedzeniu, analogicznie do istniejących mostów strukturalnych.

## Narzędzie

`--probe-chunk` (dwukrotnie, dla obu konkurujących chunków) + bezpośrednie zapytania SQL do
`messages.RetrievedSources` (jsonb) na produkcyjnej bazie, żeby zobaczyć DOKŁADNIE co model dostał
w obu turach, nie tylko co odpowiedział.
