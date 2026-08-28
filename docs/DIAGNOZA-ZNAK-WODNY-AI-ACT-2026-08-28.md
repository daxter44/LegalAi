# Diagnoza: "znak wodny" i "oznakowanie" — dwie kolizje słownictwa gubią art. 50 AI Act

Data: 2026-08-28. Punkt wyjścia: pytanie użytkownika o obowiązek oznaczania tekstu generowanego przez
LLM na podstawie AI Act. System odpowiada odmową, mimo że właściwy przepis (art. 50 ust. 2
rozporządzenia (UE) 2024/1689) jest w korpusie, zaembedowany, i aktualny (tekst skonsolidowany po
noweli 32026R1744).

## Obserwacja (dwa prawdziwe pytania z produkcji, nie sonda)

**Pytanie 1** (dosłowne sformułowanie użytkownika, potoczny termin "znak wodny"):

> „Czy tworząc aplikację opartą o duży model językowy generujący tekst - muszę oznaczać wygenerowany
> tekst znakiem wodnym? bazując na ai act"

Odpowiedź na żywo: **„Nie mam wystarczających źródeł, aby odpowiedzieć."** `Abstained=false` mimo to
— formalny mechanizm abstencji nie złapał tej odmowy (ten sam cichy fałszywy negatyw co w poprzednich
diagnozach tej serii).

**Pytanie 2** (inny prawdziwy użytkownik produkcyjny, bez słowa "znak wodny", bliżej słownictwa ustawy):

> „AI Act - czy jako dostawca rozwiązań RAG (Retrieval-Augmented Generation) moje produkty muszą
> oznaczać generowaną treść jako wygenerowane przez AI?"

Odpowiedź na żywo: odmowa merytoryczna — model cytuje **inne** przepisy AI Act (definicje, oznakowanie
CE, obowiązki dostawców systemów wysokiego ryzyka [1-7]) i explicite stwierdza, że dostarczone źródła
nie regulują tej kwestii. Art. 50 nie pojawia się wśród cytowanych źródeł wcale.

Czyli **dwa niezależne sformułowania tego samego pytania, oba realne z produkcji, oba zawodzą** — ale
z różnych powodów, co wskazuje na dwie osobne kolizje słownictwa, nie jedną.

## [FAKT — zmierzone sondą `--probe-chunk`] Art. 50 istnieje, jest aktualny, ale poza zasięgiem obu torów

Korpus zawiera art. 50 AI Act (32024R1689) w 7 chunkach (po jednym na ustęp), wszystkie zaembedowane.
Tekst jest **skonsolidowany po noweli** 32026R1744 z lipca 2026 (zweryfikowane: ust. 7 w korpusie ma
dokładnie brzmienie wprowadzone przez tę nowelę) — problem nie leży w nieaktualności treści.

Sonda dla Pytania 1, cel = art. 50 ust. 2 (dostawcy generujący syntetyczny tekst/obraz/dźwięk —
dokładnie na temat):

| Tor | Wynik |
|---|---|
| A. exact fp32 | pozycja **#2125** / 7,4 mln (sim=0,7207) |
| B. exact fp16 | pozycja #2125 (bez przesunięcia od kwantyzacji) |
| C. HNSW (ef=400) | **NIEOBECNY w top-200** |
| D. BM25 | nieobecny w top-200; tsquery (AND wszystkich słów pytania) w ogóle nie matchuje chunka |
| E. fuzja RRF | poza pulą kandydatów — odpada przed dedupem |

Dla porównania art. 50 ust. 1 (obowiązek informowania o rozmowie z chatbotem — inny temat, ale ten sam
artykuł) wypada jeszcze gorzej: pozycja #119190 exact fp32. To dokładnie ten artykuł, który w
golden-set (`ue-aiact-50`, pytanie *"Czy trzeba informować użytkownika, że rozmawia z chatbotem?"*)
jest **zweryfikowany jako trafiający** — sam artykuł jest więc osiągalny, tylko pod innym
sformułowaniem. Ranga #2125 w przestrzeni embeddingu (top 0,03% z 7,4 mln) to obiektywnie bliskie
podobieństwo semantyczne — ale całkowicie niewystarczające, żeby wejść do faktycznego okna kandydatów
(CandidatesPerPath=50).

## Mechanizm — dwie osobne kolizje, nie jedna

**Kolizja 1: "znak wodny" to termin fizyczny, nie cyfrowy.** Zapytanie do bazy pokazuje, że fraza
"znak wodny" / "znakiem wodnym" / "znaki wodne" występuje w korpusie prawie wyłącznie w kontekście
**fizycznych** zabezpieczeń dokumentów: wzory legitymacji służbowych, kart tożsamości, tabliczek
tożsamości personelu obrony cywilnej, formularzy upoważnień do zakupu paliw, orzeczenia o
sfałszowanych dokumentach. Art. 50 ust. 2 AI Act **w ogóle nie używa słowa "znak wodny"** — mówi o
treści "oznakowanej w formacie nadającym się do odczytu maszynowego" i "wykrywalnej jako sztucznie
wygenerowana lub zmanipulowana". "Znak wodny" to popularne, medialne uproszczenie tego obowiązku, nie
termin ustawowy — dokładnie ten sam mechanizm co "zaświadczenie" zamiast "oświadczenie"
(`DIAGNOZA-TERMIN-ZASWIADCZENIE-OSWIADCZENIE-2026-08-28.md`): dosłowne słowo użytkownika ma silną,
ale niewłaściwą reprezentację gdzie indziej w korpusie.

**Kolizja 2: "oznakowanie" wewnątrz samego AI Act ma dwa znaczenia.** Pytanie 2 unika słowa "znak
wodny" i mimo to zawodzi — bo AI Act samo w sobie przeciąża słowo "oznakowanie" dwoma zupełnie
różnymi obowiązkami:
- **art. 48 — "oznakowanie CE"**: formalny znak zgodności dla dostawców systemów AI **wysokiego
  ryzyka** (rozdział III) — 5 chunków w korpusie zawierających rdzeń "oznakowan".
- **art. 50 ust. 2 — oznaczanie treści jako wygenerowanej przez AI**: obowiązek przejrzystości dla
  dostawców systemów **generatywnych** (rozdział IV, dużo węższy krąg adresatów, wcale nie wymaga
  klasyfikacji "wysokiego ryzyka") — 1 chunk.

Pytanie użytkownika 2 wspomina "dostawcę" i "oznaczać" — słownictwo, które silniej ciąży w stronę
liczniejszego, bardziej "centralnego" tematycznie rozdziału III (obowiązki dostawców, oznakowanie CE,
jednostki notyfikowane) niż w stronę wąskiego art. 50. Model dostał więc realne, ale **niewłaściwe**
źródła z tej samej ustawy — nie halucynację, tylko złą sekcję tego samego aktu.

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

To trzeci zdiagnozowany w tej serii przypadek tego samego rodzaju awarii (po OKI i
zaświadczenie/oświadczenie), i pierwszy pokazujący, że kolizja może siedzieć **wewnątrz jednego aktu**,
nie tylko między dwiema różnymi dziedzinami prawa. AI Act ma szczególnie dużo takich przeciążonych
terminów ogólnych (przejrzystość, oznakowanie, nadzór, zgodność), które w mediach i w pytaniach
laików bywają używane zamiennie, a w tekście ustawy rozdzielają bardzo różne rozdziały i różne kręgi
adresatów.

## Otwarte — nieoceniona jeszcze skala i kierunek naprawy

- **Skala nieznana** (n=2 zmierzone przypadki z produkcji, nie stopa błędu na całym AI Act).
- **`Abstained=false` przy faktycznej odmowie** — powtarza się trzeci raz w tej serii diagnoz.
  Warto rozważyć osobno od kolizji słownictwa: to sygnał, że klasyfikacja "czy to odmowa" po stronie
  telemetrii/UI nie odpowiada temu, co faktycznie mówi treść odpowiedzi.
- **Kierunek naprawy nieoceniony**, te same opcje co poprzednio (słownik synonimów, query rewriting,
  poleganie na GapClosingRetrieval — które i tu nie złapało problemu, tak jak poprzednio).

## Narzędzie

`--probe-chunk` (naprawiony w tej sesji: potrzebuje `ConnectionStrings__Db` i `Embeddings__BaseUrl`
wskazane na `.11`, nie samego `PRAWORAG_DB` używanego przez `dotnet ef`) + bezpośrednie zapytania SQL
do `messages`/`chunks` na produkcyjnej bazie, zamiast sondowania na sztucznie sformułowanym pytaniu —
oba pytania są prawdziwe, zadane przez użytkowników produkcyjnych.
