# Plan: prawo UE w korpusie — konektor EUR-Lex (CELLAR)

Status: plan po spike'u dostępowym i **pomiarze wolumenu** (2026-08-26). Wszystkie liczby niżej są
z realnych odpowiedzi serwerów CELLAR/SPARQL, nie z oszacowań. Kod jeszcze nie napisany.

Kontekst decyzyjny: `PLAN-ROUTING-INTENCJI-I-WIDOCZNOSC.md` (ingestia prawa UE jako zależność
przyszłego trybu redakcyjnego) i `PLAN-ODSIEW-SZUMU-RETRIEVALU.md` (§ „Prawo UE (RODO) NIE wchodzi
w tym etapie" — pytanie 1 z zestawu było przez to nieodpowiadalne). Ten plan zdejmuje tę blokadę.

**Zakres celu (decyzja właściciela, 2026-08-26):** bierzemy **wszystko, co ma polski tekst** —
zmierzone 7 756 obowiązujących rozporządzeń i dyrektyw, z czego ~6 750 ma polską wersję. Ręczna
lista CELEX-ów służy tylko do **kolejności** (RODO i AI Act idą pierwsze, żeby dały się zmierzyć),
nie do ograniczenia zakresu.

**Zastrzeżenie z pomiaru, które zmienia sens słowa „wszystko" (§ 4.1):** 4 003 z tych 7 756 aktów
(52%) to akty ZMIENIAJĄCE inny akt, a ich tekst operacyjny to instrukcja zmiany („w załącznikach II,
III i IV do rozporządzenia (WE) nr 396/2005 wprowadza się zmiany zgodnie z załącznikiem"), nie
norma. 3 662 z nich są już WCHŁONIĘTE w teksty skonsolidowane aktów bazowych, które i tak
ingestujemy. „Wszystko po polsku" na poziomie POBIERANIA jest właściwe (metadane relacji są
potrzebne), ale na poziomie CHUNKÓW oznaczałoby korpus, w którym ponad połowa wektorów to diffy.
Rekomendacja i liczby: § 4.1.

## 1. Po co (metryka wyniku, nie mechanizmu)

**Wartość:** pytania, na których system dziś ODMAWIA, bo nie ma czego zacytować — RODO, AI Act,
DSA/DMA, DORA, MiCA, prawo konsumenckie, transportowe, produktowe, sankcje. Prawo UE jest też
warunkiem wstępnym trybu redakcyjnego (regulaminy, polityki prywatności, klauzule informacyjne).

**Metryka nadrzędna:** odsetek ODMÓW na zestawie realnych pytań z prawa UE. Zestaw powstaje PRZED
implementacją: min. 20 pytań, każde z ręcznie wskazanym przepisem docelowym; **nie tylko RODO/AI Act**
— połowa zestawu z aktów, których nazwy nie znam z prasy (transport, wyroby medyczne, sankcje,
prawo konsumenckie), bo to one weryfikują, czy korpus ma pokrycie, a nie tylko dwie gwiazdy.

**Warunek zabicia:** jeśli po zaingestowaniu **pierwszej transzy** (patrz § 5.6, UE-5.1: RODO, AI Act
+ akty z samodzielną treścią z lat 2016+, ≈1 500) odmowy na zestawie nie spadną, przyczyną nie jest pokrycie i dokładanie
kolejnych 6 500 aktów NIC nie da — wchodzimy w diagnozę retrievalu/promptu (ryzyka R5–R7). To ta
sama lekcja co z `PLAN-ROZSZERZENIE-SASIEDZTWA-AKTU.md`: więcej kontekstu ≠ odpowiedź.

## 2. Spike dostępowy i pomiar wolumenu (2026-08-26)

Wszystko odpalone `curl`-em z tej maszyny, bez klucza API i bez rejestracji.

### 2.1 Pobranie tekstu: CELLAR, nie strona EUR-Lexu

| ścieżka | wynik |
|---|---|
| `https://eur-lex.europa.eu/legal-content/PL/TXT/HTML/?uri=CELEX:32016R0679` | **HTTP 202, 0 bajtów** — strona odbija automat. Nie używamy. |
| `https://publications.europa.eu/resource/celex/{CELEX}` + `Accept: application/xhtml+xml` + `Accept-Language: pol` | **HTTP 200**, strukturalny XHTML po polsku. Ścieżka główna. |
| ta sama + `Accept: application/pdf` | **HTTP 200** dla starszych aktów bez XHTML-a. Ścieżka zapasowa. |

Rozmiary (PL, XHTML): RODO `32016R0679` 840 KB, AI Act `32024R1689` 1,33 MB, DSA `32022R2065`
886 KB, DMA `32022R1925` 563 KB — rząd wielkości jak `text.html` kodeksu z ELI, więc timeout na
próbę HTTP trzymamy jak w `EliOptions` (45 s).

Odrzucone: notice RDF aktu (`Accept: application/rdf+xml`) — **60 MB** na jeden akt i bez predykatów
konsolidacji. Bezużyteczne.

### 2.2 Wolumen: ile jest prawa UE (SPARQL, endpoint `webapi/rdf/sparql`)

Akty **obowiązujące** (`cdm:resource_legal_in-force`), po typie zasobu (`cdm:work_has_resource-type`):

| typ | ile obowiązuje | decyzja |
|---|---|---|
| **REG** — rozporządzenia | **6 647** | **w zakresie** |
| **DIR** — dyrektywy | **1 109** | **w zakresie** |
| REG_IMPL — rozporządzenia wykonawcze | 7 923 | poza v1 (flaga) |
| REG_DEL — rozporządzenia delegowane | 1 592 | poza v1 (flaga) |
| DEC_IMPL / DIR_IMPL / DIR_DEL / DEC_DEL | 3 271 / 70 / 126 / 56 | poza v1 (flaga) |
| DEC + DEC_ENTSCHEID — decyzje | 12 162 + 12 047 | poza zakresem (akty indywidualne/administracyjne) |
| TREATY / AGREE_INTERNATION / PROT / … | 6 625 / 1 111 / 552 | poza zakresem |

**Zakres v1 = REG + DIR obowiązujące: dokładnie 7 756 aktów** (pełna lista CELEX-ów pobrana
i zapisana lokalnie w spike'u; SPARQL stronicuje po 3 000 — `LIMIT/OFFSET`, przy OFFSET 9000
endpoint zwraca 500, więc pobranie musi zatrzymać się na pustej stronie, nie na błędzie).
Rozkład po latach: 1958–2003 ≈ 2 850 aktów, 2004+ ≈ 4 900 (rocznie 114–393).

**Świadomie poza v1:** akty delegowane i wykonawcze (+9 711) — to w większości techniczne załączniki
(taryfy, wykazy produktów, wzory formularzy), które zalałyby retrieval; wchodzą na flagę, najlepiej
zawężone dziedzinowo (`directory-code`/EuroVoc). Decyzje, umowy międzynarodowe i traktaty poza
zakresem. Orzecznictwo TSUE — osobne źródło i osobny plan.

### 2.3 Dostępność polskiego tekstu — POMIAR, nie założenie

Sonda na 78 losowych aktach z listy 7 756 (co 100. pozycja), realne kody HTTP:

| era | próbek | XHTML PL | tylko PDF PL | brak tekstu PL |
|---|---|---|---|---|
| 2004+ | 52 | **51** | 1 | 0 |
| przed 2004 | 26 | **0** | 17 | 9 |

Wnioski (twarde, przenoszą się na cały zakres):

1. **XHTML istnieje praktycznie tylko dla aktów od 2004 r.** — dla starszych CELLAR zwraca 404 na
   `application/xhtml+xml`. Ekstrapolacja: ~4 900 aktów przez ścieżkę XHTML.
2. **Starsze akty mają polski tekst tylko w PDF** (wydanie specjalne Dz.U. UE po akcesji) — ~65%
   próbek pre-2004. Ekstrapolacja: ~1 850 aktów przez ścieżkę PDF. **Ścieżka PDF nie jest opcją —
   bez niej tracimy jedną czwartą korpusu** (m.in. akty, na których wciąż stoi praktyka).
3. **~1 000 aktów (≈35% pre-2004) nie ma polskiego tekstu w ogóle.** To nie błąd ingestii — takie
   akty pomijamy z `QualityIssues`, i tę liczbę raportujemy jawnie, żeby „brak w korpusie" nie
   wyglądał później na porażkę parsera.
4. Manifestacja `fmx4` (Formex XML) jest w CELLAR-ze, ale negocjacja `Accept: application/xml`
   zwróciła 404 na wszystkich sondowanych aktach — **ścieżka Formex odrzucona**, zostaje XHTML + PDF.

Podsumowanie realnie osiągalnego korpusu v1: **≈6 750 aktów** (≈4 900 XHTML + ≈1 850 PDF).

**Uzupełnienie pomiaru (2026-08-26, Faza 0):** „XHTML tylko od 2004" dotyczy TEKSTU BAZOWEGO.
Dla aktów starszych XHTML potrafi istnieć w **wersji skonsolidowanej** — sprawdzone i potwierdzone
kodem 200 dla e-Privacy `02002L0058-20091219` (akt z 2002 r.), REACH `02006R1907-20260622` (5,4 MB)
i praw pasażerów lotniczych `02004R0261-20050217`, choć ich teksty bazowe zwracają 404. Na losowej
próbce 26 aktów pre-2004 ratuje to 3 akty (12%), ale trafiło w KAŻDY z trzech aktów wybranych ręcznie
jako istotne w praktyce — losowa próbka pre-2004 jest zdominowana przez akty rolne i taryfowe, więc
zaniża pokrycie tam, gdzie ono realnie boli. Wniosek dla konektora: kolejność „konsolidacja → tekst
bazowy → PDF" jest właściwa nie tylko dla aktualności, ale i dla POKRYCIA.

### 2.4 Wersje skonsolidowane: SPARQL + fallback na 404

Predykat ustalony przez enumerację krawędzi wchodzących do aktu bazowego:

```sparql
PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
SELECT ?ccelex WHERE {
  ?base cdm:resource_legal_id_celex "32024R1689"^^xsd:string .
  ?cons cdm:act_consolidated_consolidates_resource_legal ?base ;
        cdm:resource_legal_id_celex ?ccelex .
} ORDER BY DESC(?ccelex)
```

1. **Zwraca też konsolidacje OBCYCH aktów** — dla AI Act wyszły `02020L1828-20260802`,
   `02019R2144-20260802` (bo AI Act je nowelizuje). Filtr obowiązkowy: CELEX konsolidacji musi
   zaczynać się od `0` + resztę CELEX-u bazowego + `-` (`32024R1689` → `02024R1689-`).
2. **Nie każda konsolidacja istnieje po polsku:** `02024R1689-20260727` → 200 (893 KB, „TEKST
   skonsolidowany: 32024R1689 — PL — 27.07.2026"), a `02024R1689-20240712` → **404**. Konektor
   schodzi po kandydatach od najnowszego, a po wyczerpaniu bierze tekst bazowy.
3. **Konsolidacje bywają datowane w przyszłość** — bierzemy najnowszą z datą `<= dziś`, inaczej
   cytowalibyśmy prawo, które jeszcze nie obowiązuje.
4. RODO ma jedną konsolidację (`02016R0679-20160504`, sprostowanie) — 200, 490 KB.

Koszt: 1 zapytanie SPARQL na akt → 7 756 zapytań. Do zbicia: jedno zapytanie zbiorcze na transzę
(`VALUES` z listą CELEX-ów) zamiast pytania per akt.

### 2.5 Struktura tekstu — DWA warianty markupu, wspólne kotwice

| | tekst bazowy (Dz.U. UE) | tekst skonsolidowany |
|---|---|---|
| kontener artykułu | `div.eli-subdivision` + `id="art_6"` | `div.eli-subdivision` + `id="art_6"` |
| nagłówek | `p.oj-ti-art` („Artykuł 6") | `p.title-article-norm` |
| tytuł artykułu | `p.oj-sti-art` w `div.eli-title` | `p.stitle-article-norm` |
| ustęp | `div id="006.001"`, `p.oj-normal` z „1.   " | `span.no-parag` „1.  " + `div.norm` |
| litera | tabela 4%/96%, `p.oj-normal` „a)" | `div.grid-container.grid-list`, `span` „a) " |
| **śmieci do usunięcia** | `p.oj-note` (przypisy) | **`p.modref` — znaczniki `▼M1`/`▼B`** |

- **`id="art_N"` jest wspólne dla obu wariantów** → artykuły wycinamy po DOM (pewne), a podział na
  ust./lit. robimy regexem na spłaszczonym tekście artykułu (`^\d+\.` / `^[a-z]{1,2}\)`) — jak
  `ActTextParser` dla ścieżki PDF w ISAP. Jeden tor podziału na dwa warianty markupu i **ten sam tor
  działa dla ścieżki PDF** (tam nie ma DOM, ale znacznik „Artykuł N" jest).
- Artykuły mają sufiksy literowe: `art_6a`, `art_25b…d` (AI Act po noweli `32026R1744`) → numer to
  `\d+[a-z]*`.
- `p.modref` (`▼M1`) trafia w środek zdania normy — usunięcie jest warunkiem poprawności tekstu.
- AI Act bazowy ma 180 motywów (`id="rct_N"`); wersja skonsolidowana PL motywów NIE zawiera.

**Trzeci wariant, znaleziony w Fazie 0: dokumenty BEZ KOTWIC (markup „legacy").** Dokumenty
wygenerowane starszymi wersjami konwertera nie mają ŻADNYCH identyfikatorów struktury — jest tylko
tekst „Artykuł N". Losowa próbka 20 aktów (strategia „konsolidacja → tekst bazowy"):

| klasa dokumentu | ile z 20 | wersje konwertera |
|---|---|---|
| kotwice `id="art_*"` | **9 (45%)** | 9.16–9.18 |
| **legacy, bez kotwic** | **6 (30%)** | 5.4, 6.7, 6.7.1, 9.6.0 |
| brak XHTML (→ PDF albo brak PL) | 5 (25%) | — |

Konsekwencje, obie istotne dla normalizacji:
1. **Potrzebny jest TRZECI tor parsowania** — po znacznikach tekstowych „Artykuł N", czyli ten sam,
   co dla PDF-a. Wybór toru robimy po OBECNOŚCI KOTWIC w dokumencie, nie po klasie aktu ani po roku
   (`converter_version` z komentarza HTML idzie do metadanych jako diagnostyka).
2. **Realny konflikt aktualność vs. struktura.** Dla `32013L0053` tekst skonsolidowany jest legacy
   (0 kotwic), a tekst BAZOWY ma 116 kotwic. Wybór „weź to, co lepiej się parsuje" oznaczałby
   serwowanie prawa w brzmieniu przed zmianami. Decyzja zgodna z linią ISAP (gdzie po t.j. bierzemy
   nawet PDF): **aktualność wygrywa**, tekst skonsolidowany parsujemy torem tekstowym.

## 3. Decyzje projektowe

**D1. Odkrywanie po SPARQL, nie ręczna lista.** Zakres definiuje **zapytanie** (typ zasobu ∈ {REG,
DIR}, obowiązujący, rok od–do), tak jak `Eli:Discover` definiuje zakres ISAP-u. Ręczna lista CELEX
(`Acts`) zostaje jako **priorytet kolejności** (RODO, AI Act i inne akty z zestawu pomiarowego idą
w pierwszej transzy) oraz jako awaryjne dokładanie pojedynczych aktów spoza filtra.

**D2. Transze, nie jeden przebieg.** 6 750 aktów to przebieg na godziny (pobranie + embedding).
Ingestujemy transzami malejącej istotności: (T1) lista priorytetowa + rok ≥ 2016, (T2) 2004–2015,
(T3) pre-2004 przez PDF. Po T1 mierzymy metrykę z § 1 — to jest warunek zabicia, a nie „skończmy
całość i zobaczmy".

**D3. Dwie ścieżki treści: XHTML → PDF.** Kolejno: najnowsza konsolidacja PL (XHTML), tekst bazowy
PL (XHTML), tekst bazowy PL (PDF, przez istniejący `PdfPigTextExtractor`). Brak wszystkiego = akt
pomijany + wpis w raporcie „bez tekstu PL" (≈1 000 aktów — liczba znana z pomiaru, nie zaskoczenie).

**D4. Ingestujemy TEKST AKTUALNY, tożsamość aktu = CELEX bazowy.** `externalId` = CELEX bazowy
(stabilny w czasie); użyta wersja w metadanych (`textVersion: consolidated|base|pdf`,
`consolidatedCelex`, `consolidationDate`). Nowa konsolidacja = zmiana `content_hash` = podmiana
chunków przez istniejący `IngestionPipeline`, bez duplikatu dokumentu — jak „tekst jednolity" w ISAP.

**D5. Motywy (preambuła) poza v1.** Wykładnia, nie norma; w polskich tekstach skonsolidowanych ich
nie ma, więc wymagałyby drugiego pobrania i drugiego dokumentu na akt. Dokładamy, jeśli pomiar
pokaże, że to ICH brak powoduje odmowy. Zapisane wprost, żeby nie wyglądało na przeoczenie.

**D6. Granulacja chunków: artykuł → ustęp → litera.** Zasada „jeden wektor = jedna norma"
z `ActNormalizer` (zmierzone na art. 52 § 1 KP: mieszanie podstaw rozmywa cosine o ~0,15).
Mapowanie na `CitationLocator`: `Article`=„6", `Paragraph`=„1" (ust.), `Point`=„f" (lit.).
Nagłówek kontekstowy: `RODO, Rozdział II, art. 6 ust. 1 lit. f)`.

**D7. Nowe źródło, kanoniczny typ „akt".** `SourceKeys.EurLex = "EURLEX"`, selektor normalizera
`DocTypes.EuAct = "eu-act"`, `norm.DocType` = `DocTypes.Act` — wzorem NSA (`nsa-judgment` →
`judgment`). Retrieval widzi akt prawny, filtry po `doc_type` bez zmian.

**D8. Uprzejmość wobec CELLAR-a.** 6 750 pobrań × ~0,5 MB. Limit równoległości (domyślnie 2),
przerwa między żądaniami, wznawialność z magazynu surowych (`IRawDocumentStore` — akt raz pobrany
nie jest pobierany ponownie). Bez tego ryzykujemy odcięciem po IP w środku transzy.

**D9. Licencja i atrybucja.** EUR-Lex/CELLAR: treść CC-BY 4.0, metadane CC0 (najczystsza licencja
w korpusie — `PLAN.md`). `SourceUrl` = `https://eur-lex.europa.eu/legal-content/PL/TXT/?uri=CELEX:{celex}`
(link dla człowieka; treść bierzemy z CELLAR-a) + wzmianka o CC-BY w stopce panelu źródeł.
Dane pozostają w UE — zgodne z zasadą PL/UE.

## 4. Normalizacja: co jest normą, co metadanymi, a co śmieciem

Pytanie „jak nie wciągnąć śmieci" ma w tym źródle TRZY poziomy i tylko jeden z nich to markup.
Kolejność jest istotna: filtr markupu nie naprawi tego, że wciągnęliśmy 4 000 aktów, których
tekst jest instrukcją zmiany. Wszystkie liczby niżej są zmierzone 2026-08-26 (SPARQL na całej
populacji + próbka 18 losowych aktów z lat 2004+).

### 4.1 Poziom 1 (największy): akty bez samodzielnej treści normatywnej

Rozkład populacji 7 756 obowiązujących rozporządzeń i dyrektyw:

| klasa | ile | co niesie tekst |
|---|---|---|
| **zmieniające inny akt** | **4 003 (52%)** | instrukcję zmiany; **3 662 z nich są już wchłonięte w teksty skonsolidowane**, które ingestujemy osobno |
| uchylające inny akt | 946 | jedno zdanie („traci moc") + data |
| ani zmieniające, ani uchylające | **3 138** | treść własną — to trzon prawa materialnego |

Dowód z próbki (18 losowych aktów 2004+): tytuły to w większości „zmieniające…", „uchylające…",
„dostosowujące…", „wprowadzające odstępstwo…", „otwierające kontyngent taryfowy…". Treść art. 1
aktu zmieniającego wygląda dosłownie tak:

> Artykuł 1 — W załącznikach II, III i IV do rozporządzenia (WE) nr 396/2005 wprowadza się zmiany
> zgodnie z załącznikiem do niniejszego rozporządzenia.

Dla RAG to najgorszy możliwy chunk: **wygląda jak przepis, cytuje się jak przepis, a nie odpowiada
na żadne pytanie** — i jednocześnie duplikuje treść, która jest już w tekście skonsolidowanym aktu
bazowego, tylko w formie różnicowej (ryzyko odpowiedzi „stan przed zmianą" przy trafieniu w diff).

### 4.1a Poprawki reguły selekcji — trzy błędy złapane w Fazie 1 (2026-08-26)

Pierwotna reguła („zmienia albo uchyla → tylko metadane") była za gruba i każda iteracja wychodziła
na realnych danych, nie w rozumowaniu. Zapisane, bo to jest właściwa treść tej decyzji:

1. **„Uchyla" nie odbiera treści.** Realna odpowiedź SPARQL-a pokazała, że **RODO uchyla dyrektywę
   95/46/WE** — tak samo GPSR uchyla starą dyrektywę o bezpieczeństwie produktów, a MDR dyrektywy
   o wyrobach medycznych. Reguła „uchyla → metadane" wyrzuciłaby z wektorów najważniejsze akty korpusu.
2. **„Zmienia + wchłonięte" też nie wystarcza.** Przebieg bramkowy Fazy 1 zaklasyfikował jako
   metadane-only **AI Act, DSA, DMA, REACH i MDR** — bo akty merytoryczne zmieniają inne akty
   w przepisach końcowych, a te zmiany są wchłaniane w konsolidacje tamtych aktów.
3. **Rozstrzyga POZYCJA imiesłowu w tytule**: akt czysto nowelizujący ma „zmieniające…" na pozycji
   czasownika, PRZED jakimkolwiek własnym „w sprawie…" (w jego tytule „w sprawie" należy do aktu
   ZMIENIANEGO). Tu wyszedł trzeci błąd: **tytuły z CELLAR-a niosą twarde spacje (U+00A0)**, więc
   dopasowanie „w sprawie" nie trafiało i REACH z dyrektywą konsumencką znów wypadały jako nowele.

Zmierzone liczby po poprawce (SPARQL na populacji, filtr pozycyjny po tytule):

| miara | ile |
|---|---|
| obowiązujące REG+DIR z **polskim tytułem** (≈ ingestowalny wszechświat) | **6 760** |
| akty czysto nowelizujące (imiesłów przed „w sprawie") | 2 858 |
| **z tego wchłonięte w konsolidacje = JEDYNY zbiór „tylko metadane"** | **2 674** |
| akty niosące treść do wektorów (7 756 − 2 674) | **5 082** (z polskim tekstem ≈ 4 086) |

Reguła zaimplementowana w `EuActClassifier` i pokryta testami na realnych tytułach — z osobnym
testem regresji na każdy z trzech powyższych błędów.

**Rekomendacja.** Rozdzielić POBIERANIE od CHUNKOWANIA:
- pobieramy i utrzymujemy metadane dla wszystkich odkrytych aktów (relacje zmienia/uchyla są
  potrzebne do aktualności i do wyjaśniania, skąd wzięła się treść skonsolidowana);
- **chunkujemy tylko akty z samodzielną treścią** (≈3 138, po odsiewie braku polskiej wersji
  ≈2 700–2 800) oraz akty zmieniające, których zmiany NIE są jeszcze wchłonięte w żadną konsolidację
  (4 003 − 3 662 = **341** — to jedyne diffy, które realnie dokładają aktualnej treści);
- akty uchylające: bez chunków, sam fakt uchylenia jako metadana.

To dokładnie ta decyzja, którą projekt podjął już dla ISAP-u: `EliDiscoverOptions.Statuses`
świadomie pomija „akt objęty tekstem jednolitym" z uzasadnieniem „akty nowelizujące wchłonięte do
tekstu bazowego — niska wartość samodzielna, ryzyko dubli/starej treści". Tu jest to samo zjawisko,
tylko 4 000 razy.

**Koszt tej rekomendacji (jawnie):** tracimy z wektorów akty czysto techniczne, które dla części
praktyki SĄ prawem — kontyngenty taryfowe, limity pozostałości pestycydów, specyfikacje ChNP,
listy sankcyjne. Jeśli to ma wejść, właściwa forma to nie „wrzućmy wszystko", a **znacznik klasy
aktu** (`actClass: substantive | amending | technical | repealing`) w metadanych i osobna decyzja,
czy retrieval ma tę klasę zaniżać, czy filtrować. Znacznik jest tani i odwracalny; masowe chunkowanie
diffów nie jest.

### 4.2 Poziom 2: jednostki-bojlerplate powtórzone w całym korpusie

W próbce **15 z 17** aktów kończy się tymi samymi dwoma formułami: „Niniejsze rozporządzenie wchodzi
w życie … dnia po jego opublikowaniu w Dzienniku Urzędowym Unii Europejskiej" oraz „Niniejsze
rozporządzenie wiąże w całości i jest bezpośrednio stosowane we wszystkich państwach członkowskich".
W skali korpusu to **~12–13 tysięcy niemal identycznych chunków**.

To nie jest problem estetyczny — to zmierzony w tym projekcie mechanizm porażki. `ChunkDegeneracy`
powstał po raporcie odmów z 2026-07-18/19, gdzie 1 056 chunków `(pominięt*)` i szum anonimizacyjny
SAOS „mają anomalnie »lepkie« embeddingi i wypychają realne przepisy ze źródeł". Bojlerplate UE to
ta sama klasa, o rząd wielkości większa.

**Rekomendacja:** rozpoznawać formuły końcowe wzorcem i nie tworzyć z nich chunków (data wejścia
w życie i tak jest metadaną aktu, nie treścią do cytowania). Dodatkowo rozszerzyć słownik
`ChunkDegeneracy.PlaceholderVocabulary` o formy unijne („usunięty", „skreślony", „uchylony" jako
całość jednostki), bo teksty skonsolidowane zostawiają puste artykuły po zmianach.

### 4.3 Poziom 3: śmieci wewnątrz dokumentu (markup i format)

Zinwentaryzowane na realnych plikach — to zamknięta lista, każda pozycja z konkretnym markupem:

| co | gdzie | dlaczego szkodzi |
|---|---|---|
| nagłówek Dz.U. UE („4.5.2016 / PL / Dziennik Urzędowy / L 119/1") | `p.oj-hd-*` | wchodzi w pierwszy chunk aktu jako „treść" |
| **znaczniki wersji `▼M1`, `▼B`, `►C1`** | `p.modref`, `p.arrow` | stoją **w środku zdania normy** w tekstach skonsolidowanych |
| klauzula informacyjna tekstu skonsolidowanego | `p.disclaimer` | „nie ma mocy prawnej" wklejone do aktu, który cytujemy jako prawo |
| linia wersji („02016R0679 — PL — 04.05.2016 — 000.003") | `p.reference` | wygląda jak sygnatura, nie znaczy nic dla użytkownika |
| przypisy do innych Dz.U. | `p.oj-note`, wtrącenia `( 1 )` | rwą zdanie; w akcie tabelowym zmierzone **745 przypisów** |
| formuła podpisowa („W imieniu Rady / Przewodniczący") | `p.oj-signatory` | 4–8 wystąpień w każdym akcie, zero treści |
| nagłówki/stopki stron i przeniesienia wyrazów | ścieżka PDF | „01/t. 1 PL Dziennik Urzędowy Unii Europejskiej 65" w środku normy; „sto-/sowane" |
| przeplot dwóch kolumn | ścieżka PDF | norma skleja się z sąsiednią kolumną — treść staje się nieprawdziwa |

**Rekomendacja: biała lista kontenerów, nie czarna lista śmieci.** Chunki powstają wyłącznie
z kontenerów o znanych identyfikatorach (`art_*`, `anx_*`, ewentualnie `rct_*`), a wszystko poza
nimi jest z definicji oprawą. Uzasadnienie: w CELLAR-ze widzieliśmy już DWIE wersje konwertera
(`9.16.1` i `9.18.0`) i dwa różne warianty markupu; czarna lista przy zmianie schematu **cicho
przepuszcza** nowy śmieć, biała lista **głośno pada** (0 artykułów → `QualityIssues` → widać
w raporcie). Ta sama zasada uratowała ISAP przy `pro-cite-text`.

### 4.4 Odwrotna strona tego samego pytania: co CICHO ZNIKNIE

Dwa mechanizmy, które nie brudzą korpusu, ale go okradają — trzeba je zaadresować w tym samym
kroku, bo inaczej „czysty" korpus okaże się niepełny:

1. **Załączniki.** Treść załączników siedzi w `div id="anx_I"`, `anx_II`, … — poza `art_*`.
   Parser oparty tylko na artykułach **milcząco gubi cały załącznik III do AI Act** (wykaz systemów
   wysokiego ryzyka), czyli najczęściej pytaną tabelę tego rozporządzenia. Załączniki muszą wejść
   z własnym lokalizatorem („załącznik III pkt 5 lit. b"), a nie zostać doklejone do ostatniego
   artykułu. Osobna decyzja dotyczy tabel kodowych (kody CN, wartości MRL): w akcie o pozostałościach
   pestycydów tabele to 34% tekstu, a wiersz „ | | (12) | | | " nie jest odpowiedzią na żadne pytanie
   — proponuję próg treściowy na wiersz (jak `MinSubstantiveWords`), nie chunk na wiersz.
2. **Fałszywe „brak wersji polskiej".** CELLAR na `Accept: application/xhtml+xml` zwrócił dla
   `32004L0029` **HTTP 404 z komunikatem** „cellar identifier … does not hold a content datastream
   of the requested type", a ten sam akt w PDF-ie oddał **200 i 218 KB**. Gdyby ścieżka PDF była
   wyłączona albo gdyby 404 był interpretowany jako „nie ma po polsku", akt zniknąłby bez śladu.
   Wniosek: 404 na jednym formacie NIE jest wnioskiem o braku języka, a 214-bajtowa odpowiedź
   z komunikatem błędu nie może trafić do magazynu jako dokument (próg minimalnej długości treści).

### 4.5 Motywy (preambuła) — decyzja do podjęcia

Motywy to nie norma, ale bywają jedyną wykładnią (AI Act ma ich 180, w tekstach skonsolidowanych
PL nie ma ich wcale). Trzy opcje, z konsekwencjami: (a) **bez motywów** — korpus czysty, tracimy
wykładnię i część pytań „dlaczego"; (b) **motywy jako osobny typ segmentu** z lokalizatorem
„motyw N" i możliwością zaniżenia w retrievalu — najdroższe, bo wymaga drugiego pobrania (tekst
bazowy) dla aktów, które mają konsolidację; (c) motywy razem z artykułami — najgorsze, bo mieszają
argumentację z normą w jednej przestrzeni wektorowej. Rekomendacja: (a) na start, (b) tylko jeśli
pomiar odmów wskaże brak motywów jako przyczynę.

### 4.6 Jak sprawdzić, że odsiew działa (bez wiary w projekt)

Trzy artefakty, każdy tani i konkretny:
1. **Raport per akt** (rozszerzenie `QualityReportRunner`): liczba artykułów i załączników, liczba
   jednostek odrzuconych i dlaczego, klasa aktu, wersja tekstu (skonsolidowany/bazowy/PDF).
   Akt z zerem artykułów to alarm, nie wpis w logu.
2. **Raport duplikatów na korpusie:** 50 najczęściej powtarzanych tekstów chunków. To wykrywa
   NOWĄ klasę bojlerplate'u, o której dziś nie wiemy — dokładnie tak znalazł się szum SAOS.
3. **„Śmieci w źródłach" w pomiarze odpowiedzi:** dla zestawu pytań z § 1 liczymy, ile pokazanych
   źródeł jest bojerplate'em, diffem albo wierszem tabeli. To metryka WYNIKU odsiewu — jeśli jest
   zerowa, dalsze dokręcanie filtrów nie ma sensu; jeśli wysoka, wiemy której klasy dotyczy.

## 5. Plan implementacji

Decyzje właściciela (2026-08-26), na których stoi ten plan: pobieramy wszystko, co ma polski tekst;
**chunkujemy tylko akty z samodzielną treścią** oraz zmiany niewchłonięte (§ 4.1); akty zmieniające
wchłonięte i uchylające zostają jako metadane; tabele w załącznikach z progiem treściowym na wiersz,
nie chunk na wiersz (§ 4.4); motywy poza pierwszą wersją (§ 4.5).

Zasada przenoszona z SAOS i ELI, nie do negocjacji w tym planie: **każda faza kończy się artefaktem
z liczbą**, a nie „działa". Trzy razy w tym projekcie mechanizm był poprawny, a wynik zerowy (polski
FTS, rozszerzenie sąsiedztwa, kotwice sygnatur) — dlatego bramki są wpisane w fazy, nie dodane na końcu.

### 5.0 Co bierzemy z toru SAOS/ELI bez zmian (i czego NIE dotykamy)

Reuse (żadnej nowej infrastruktury):
- **dwie fazy `fetch` / `process`** + `IRawDocumentStore` — raz pobrany akt przetwarzamy offline,
  ile razy chcemy (zmiana normalizera nie oznacza 6 750 pobrań);
- **idempotencja pipeline'u**: `(source, externalId)` + `content_hash` + `status=Indexed`; nowa
  konsolidacja = nowy hash = transakcyjna podmiana chunków, bez duplikatu dokumentu;
- **kwarantanna per dokument + bezpiecznik serii** (ODP-2/ODP-3): akt psuje się sam, nie wywala
  przebiegu; seria porażek przerywa run kontrolowanie; `FailureReport` z etapem;
- **`ChunkDegeneracy` + `MinSubstantiveWords`** — istniejący odsiew pustych i „lepkich" chunków,
  rozszerzany o formy unijne (§ 5.3);
- **`QualityReportRunner`** — raport normalizacji BEZ embeddingu i bazy, uruchamiany przed masowym
  przebiegiem (tak jak przed ingestią ISAP);
- **`golden-set.json` + `--exam` + `--refusals`** jako gotowy tor pomiaru; `expectedEli` przyjmuje
  CELEX, bo lokalizator aktu UE ustawia `EliId = CELEX` (ten sam mechanizm, co `DU/1997/553`);
- **wzorzec `sync-eli`** dla delty dziennej i **`AmendmentRelinkRunner`** jako precedens dla
  odświeżania relacji w stanie ustalonym;
- **`DisambiguateDuplicateUnits`** jako precedens dla dwóch brzmień tej samej jednostki.

Świadomie NIE dotykamy: `PdfPigTextExtractor` (tor ISAP — osobna klasa dla PDF-ów UE, § 5.6),
`JudgmentNormalizer`, `ActNormalizer`, chunkera. Zmiany w kodzie wspólnym są tylko dwie i obie
w Fazie 4 (`ActAliases`, `CitationParser`) — każda z testami równoważności PRZED zmianą.

### 5.1 Faza 0 — pomiar bazowy (przed pierwszą linią kodu produkcyjnego)

| zadanie | co powstaje | bramka |
|---|---|---|
| **UE-0.1** 20 pytań UE do `golden-set.json` | wpisy z `expectedEli` = CELEX i `expectedArticle`; kategorie jak dziś: `InCorpus`, `Trap`, `RelatedButWrong`, `OutOfCorpus`; **min. 10 pytań poza RODO/AI Act** (transport, wyroby medyczne, sankcje, prawo konsumenckie, żywność) | zestaw zamrożony w repo |
| **UE-0.2** przebieg „przed" | `docs/POMIAR-PRAWO-UE-PRZED.md`: `--exam` + `--refusals` na dzisiejszym korpusie | pytania UE = odmowa/brak trafienia (jeśli któreś przechodzi, to sygnał, że model odpowiada z pamięci parametrycznej — osobny problem, do zapisania) |
| **UE-0.3** baseline polski | ten sam raport dla istniejących pytań PL | liczba, względem której mierzymy regresję po każdej transzy |

Bez artefaktu z UE-0.2 nie startuje Faza 2. To jest dokładnie ta bramka, której zabrakło przy
polskim FTS (6,5 h pracy przy zerowym efekcie, bo metryka wyniku powstała po fakcie).

### 5.2 Faza 1 — zakres i klasyfikacja aktów (SPARQL, bez pobierania treści)

- **UE-1.1 Konfiguracja i klucze.** `EurLexOptions` (BaseUrl CELLAR, SparqlUrl, `Language: pol`,
  `Acts` = kolejność priorytetowa, `Discover`: `ResourceTypes [REG, DIR]`, `InForceOnly`,
  `YearFrom/YearTo`, `PageSize`, `RequestDelayMs`, `MinContentBytes`), `SourceKeys.EurLex`,
  `DocTypes.EuAct`. DI: typowany `HttpClient` + `AddStandardResilienceHandler` — kopia wzorca ELI.
- **UE-1.2 `EurLexSparql` (funkcje czyste, zero sieci).** Budowa i parsowanie czterech zapytań:
  zakres (typ + `in-force` + rocznik, `LIMIT/OFFSET`), konsolidacje (`VALUES`, porcjami),
  relacje `amends` / `repeals` (porcjami), oraz wybór kandydatów tekstu (filtr prefiksu CELEX-u
  + data `<= dziś` + tekst bazowy na końcu). Testy na **zapisanych realnych odpowiedziach**
  endpointu (fixture), bo to tu siedzą trzy zmierzone pułapki z § 2.4.
  Uwaga wdrożeniowa: stronicowanie kończy pusta strona **albo** błąd 500 (zmierzone przy OFFSET 9000).
- **UE-1.3 Klasyfikator klasy aktu.** Czysta funkcja z relacji → `actClass`:
  `substantive` (nie zmienia i nie uchyla), `amending-absorbed` (zmienia + istnieje konsolidacja
  wchłaniająca), `amending-open` (zmienia, brak konsolidacji), `repealing`. Testy: akt zmieniający
  i uchylający jednocześnie, akt zmieniający wiele aktów, akt bez relacji.
- **UE-1.4 Tryb `discover` dla EURLEX.** Raport wolumenu per klasa i rocznik BEZ pobierania treści.
  Artefakt: `docs/POMIAR-ZAKRES-UE.md`.

**Bramka Fazy 1 — PRZESZŁA (2026-08-26).** Trzy przebiegi na żywym endpoincie, każdy wykrył błąd
opisany w § 4.1a; po poprawkach przebieg na roczniku 2025 (182 akty: lista priorytetowa + odkryte)
dał skład **104 substantive / 74 amending-absorbed / 4 amending-open**, zero aktów bez tytułu PL,
a RODO, AI Act, DSA, DMA, e-Privacy, REACH, MDR, transport i oznaczanie żywności są po stronie
„treść + chunki". Udział metadane-only (74/182 ≈ 41%) zgadza się z pomiarem populacyjnym
(2 674/6 760 ≈ 40%).

Dodatkowo złapane i naprawione w tej fazie (oba były cichymi zabójcami przebiegu):
- **pętla stronicowania bez końca**, gdy endpoint zignoruje `OFFSET` i oddaje w kółko tę samą stronę
  (test tego przypadku nie zakończył się w 10 minut, zanim powstał bezpiecznik „strona nie wniosła
  nic nowego");
- **cicha degradacja metadanych**: CELLAR pod obciążeniem zwraca **502**, a brak odpowiedzi wyglądał
  jak „brak konsolidacji" — czyli tekst bazowy (stare prawo) i klasa „substantive" na wiarę.
  Teraz akt dostaje flagę `MetadataDegraded`, a przebieg wypisuje ostrzeżenie; Faza 2 nie może
  pobierać treści dla takich aktów bez powtórzenia odkrywania.

### 5.3 Faza 2 — pobieranie do magazynu surowych

- **UE-2.1 `EurLexConnector : ISourceConnector`.** Kolejność: XHTML najnowszej polskiej konsolidacji
  → XHTML tekstu bazowego → PDF tekstu bazowego → pominięcie z wpisem w raporcie. `RawDocument`
  z `SourcePayload` = `{celex, textCelex, textVersion, actClass, amends[], repeals[], resourceType, year}`.
- **UE-2.2 Dwa zabezpieczenia wyniesione z pomiaru** (bez nich korpus dostaje śmieci lub traci akty):
  **404 na jednym formacie nie jest wnioskiem o braku języka** (zmierzone: `32004L0029` — XHTML 404,
  PDF 200 i 218 KB) oraz **próg `MinContentBytes`** odsiewający odpowiedzi-komunikaty (214 bajtów
  „does not hold a content datastream").
- **UE-2.3 Treść tylko dla klas, które ją niosą.** `substantive` + `amending-open` → pełna treść;
  `amending-absorbed` + `repealing` → wyłącznie metadane i relacje (bez pobierania treści). Oszczędza
  ~3 900 pobrań i, co ważniejsze, nie wpuszcza diffów do magazynu, skąd trafiłyby do chunków przy
  pierwszym nieostrożnym reprocessingu.
- **UE-2.4 Uprzejmość i wznawialność.** `RequestDelayMs`, brak zrównoleglenia powyżej 2, skip po
  magazynie, obserwacja 429/503 w logu, bezpiecznik serii porażek (`FailStreakLimit`).

**Bramka Fazy 2:** transza próbna 200 aktów — zero 429/503, udział aktów bez wersji polskiej
zgodny z prognozą (~13% całości, ~35% dla pre-2004). Rozjazd = zmiana po stronie CELLAR-a
i przeliczenie prognozy przed masowym przebiegiem.

### 5.4 Faza 3 — normalizacja (biała lista kontenerów)

- **UE-3.0 Wybór toru parsowania (nowe, z pomiaru Fazy 0).** Trzy tory, wybierane po zawartości
  dokumentu, nie po klasie aktu: (a) kotwice `id="art_*"` → tor DOM (45% próbki); (b) brak kotwic,
  jest tekst „Artykuł N" → tor tekstowy (30% próbki — markup legacy ze starych konwerterów);
  (c) ani jedno, ani drugie → `QualityIssue` i pominięcie. `converter_version` do metadanych.
  Tor tekstowy jest ten sam, co dla PDF-a, więc to nie trzeci parser, a trzecie wejście do jednego.
- **UE-3.1 `EuActNormalizer`.** Selektor `eu-act`, kanoniczny typ `act` (wzorem NSA). Chunki tylko
  z kontenerów `id="art_*"` i `id="anx_*"`; oba warianty markupu (`oj-*` i `*-norm`) po wspólnej
  kotwicy `id`. Usuwane węzły: `p.modref` (`▼M1`/`▼B`), `p.arrow`, `p.oj-note`, `p.disclaimer`,
  `p.reference`, `p.oj-signatory`, nagłówki `p.oj-hd-*`. Nagłówek artykułu zdejmowany z treści,
  podtytuł artykułu i rozdział → nagłówek kontekstowy chunka (jak w `ActNormalizer`).
- **UE-3.2 `EuActUnitSplitter`.** Granulacja artykuł → ustęp → litera/punkt na spłaszczonym tekście;
  podpunkty rzymskie `(i)`/`(ii)` zostają przy swojej literze; wstęp przed wyliczeniem osobną
  jednostką (zmierzone na KP: doklejanie wstępu do każdej litery obniża cosine). Jeden tor dla obu
  wariantów XHTML i dla ścieżki PDF.
- **UE-3.3 Załączniki.** Własny lokalizator (`załącznik III pkt 5 lit. b`), nie doklejanie do
  ostatniego artykułu. Wiersze tabel przechodzą tylko przez próg treściowy (jak `MinSubstantiveWords`).
  Test obowiązkowy: załącznik III do AI Act (wykaz systemów wysokiego ryzyka) jest w korpusie
  i ma poprawny lokalizator.
- **UE-3.4 Odsiew bojlerplate'u.** Formuły końcowe („wchodzi w życie…", „wiąże w całości i jest
  bezpośrednio stosowane…") nie tworzą chunków; data wejścia w życie idzie do metadanych.
  `ChunkDegeneracy` rozszerzony o „usunięty/skreślony/uchylony" jako całość jednostki.
- **UE-3.5 Dwa brzmienia tej samej jednostki** (precedens `DisambiguateDuplicateUnits`): oznaczenie
  wariantów + `QualityIssue`, bez zgadywania, które obowiązuje.
- **UE-3.6 Raport jakości dla EURLEX.** Per akt: liczba artykułów i załączników, liczba jednostek
  odrzuconych z podziałem na powód, klasa aktu, wersja tekstu. **Zero artykułów = alarm**, nie linia w logu.

**Bramka Fazy 3:** raport jakości na 100 aktach z różnych roczników + ręczny przegląd 10 losowych
chunków (czy da się je zacytować prawnikowi bez zażenowania). Fixture'y do testów to realne pliki
z CELLAR-a: RODO bazowe, RODO skonsolidowane, AI Act (z `▼M1` i artykułem z sufiksem literowym),
akt z dużym załącznikiem tabelowym.

### 5.5 Faza 4 — retrieval rozpoznaje prawo UE

- **UE-4.1 `ActAliases`**: nazwy zwyczajowe → oznaczenie aktu obecne w tytule z CELLAR-a
  („RODO" → `2016/679`), plus przepuszczenie samego oznaczenia. Ta ścieżka wchodzi w istniejące
  `ResolveActAsync` (ILike po tytule) — bez nowego toru w retrievalu.
- **UE-4.2 `CitationParser`**: jednostka `ust.` obok `§`, oznaczenia `(UE) 2016/679` i `95/46/WE`,
  nazwy zwyczajowe. **Testy równoważności na polskich cytatach PISANE PRZED zmianą** — regresja tu
  uderza w cały dotychczasowy korpus, nie w prawo UE.
- **UE-4.3 Spójność lokalizatora z pomiarem**: `EliId = CELEX` musi zamykać ścieżkę
  `golden-set.expectedEli` → `ExamRunner` (test na wpisie UE z Fazy 0).
- **UE-4.4 `actClass` w metadanych** i decyzja (osobna, po pomiarze) czy klasa `technical` ma być
  zaniżana w rankingu. Na tym etapie tylko znacznik.

### 5.6 Faza 5 — transze i pomiar (tu rozstrzyga się sens całości)

- **UE-5.1 T1:** lista priorytetowa + `substantive` z lat ≥ 2016 (≈1 500 aktów). Po niej pełny
  pomiar: `--exam`, `--refusals`, plus **„śmieci w źródłach"** (ile pokazanych źródeł to bojlerplate,
  diff albo wiersz tabeli). Artefakt: `docs/POMIAR-PRAWO-UE-PO.md`.
  **Warunek zabicia:** brak spadku odmów → przyczyną nie jest pokrycie; wchodzimy w R5–R7, nie
  w kolejne transze.
- **UE-5.2 T2:** `substantive` 2004–2015.
- **UE-5.3 Regresja polska po każdej transzy** — `golden-set` + `refusal-set` na pytaniach PL.
  Regresja jest warunkiem STOPU dla kolejnej transzy (R7: 6 750 nowych aktów rozmywa retrieval PL).
- **UE-5.4 `sync-eurlex`** (analog `sync-eli`): nowe akty i **nowe konsolidacje** (aktualność treści),
  plus relink relacji — akt `amending-open`, którego zmiana została wchłonięta, przechodzi do
  metadata-only i jego chunki są usuwane.

### 5.7 Faza 6 — pre-2004 przez PDF (warunkowa, osobna wycena)

Wejście tej fazy jest już zmierzone jako problem (R4): `Page.Text` zwraca dla wydania specjalnego
tekst bez spacji, a czytanie w kolejności strumienia przeplata dwie kolumny.

- **UE-6.1 Spike** czytania geometrycznego (linie po Y, kolumny po X, scalanie przeniesień) na
  30 aktach z różnych roczników. Kryterium zdania: znaczniki „Artykuł N" wykrywalne w ≥90% próbki
  i brak przeplotu kolumn w ręcznym przeglądzie 5 aktów.
- **UE-6.2** Jeśli spike zdany — osobna klasa ekstraktora (tor ISAP bez zmian) + transza T3.
  Jeśli nie — **pre-2004 nie wchodzi i to jest jawna dziura ~1 850 aktów**, zapisana w raporcie
  pokrycia, nie przemilczana.

### 5.8 Zależności, złożoność i gdzie można się zakopać

Kolejność wymuszona: Faza 0 → 1 → 2 → 3 → 4 → 5; Faza 6 równolegle po Fazie 3, ale przed T3.
Wewnątrz Fazy 3 zadania UE-3.1–3.3 idą razem (jeden normalizer), 3.4–3.6 mogą po nich.

Gros roboty: **UE-3.1–3.3** (dwa warianty markupu + załączniki) i **UE-5.x** (przebiegi i pomiary,
koszt maszynowy, nie umysłowy). Zadania małe: UE-1.1, UE-1.3, UE-3.4, UE-4.1.

Ryzyko zakopania się (nie „długo", a „można utknąć bez końca"):
1. **Tabele w załącznikach** — nieskończona liczba układów; twardy próg treściowy i zgoda na to,
   że tabele kodowe zostają poza wektorami, są tu ochroną przed rabbit hole.
2. **Ścieżka PDF** — geometria stron wydania specjalnego bywa nieregularna; dlatego spike z kryterium
   zdania PRZED wpisaniem tej ścieżki w zakres.
3. **Relacje w CELLAR-ze** — jeśli bramka Fazy 1 nie odtworzy liczb, selekcja z § 4.1 traci podstawę;
   to jedyne miejsce, w którym warto dopytać SPARQL-a dalej, zamiast obchodzić problem heurystyką
   na tytułach („zmieniające…”).

## 6. Ryzyka

- **R1. Wolumen embeddingu.** ≈6 750 aktów × dziesiątki–setki chunków ≈ setki tysięcy wektorów.
  Mitygacja: transze, pomiar po T1, magazyn surowych (re-embed bez ponownego pobierania).
- **R2. Odcięcie przez CELLAR** przy 6 750 pobraniach. Mitygacja: D8 (limit równoległości, przerwy,
  wznawialność). Do obserwacji w T1: czy pojawiają się 429/503.
- **R3. Brak tekstu PL dla ~1 000 aktów** (zmierzone). Mitygacja: pomijanie z raportem, nie
  udawanie kompletności; ewentualne uzupełnienie z wydania specjalnego Dz.U. UE w osobnym kroku.
- **R4. Ścieżka PDF wymaga INNEGO ekstraktora — zmierzone, nie hipotetyczne.** Na realnym akcie
  (CELEX 31973R1545, wydanie specjalne) obecny `PdfPigTextExtractor` (`Page.Text`) zwrócił tekst
  BEZ SPACJI („ROZPORZĄDZENIE(EWG)NR1545/73RADYzdnia…"), a czytanie słów w kolejności strumienia
  przeplata dwie kolumny, czyli skleja normę z sąsiednią kolumną. Ścieżka PDF (~1 850 aktów) jest
  więc realna tylko z czytaniem geometrycznym (linie po Y, podział na kolumny po X, scalanie
  przeniesień „sto-"+„sowane"), i to jest osobna praca do wyceny, nie efekt uboczny konektora.
  Warunek wejścia transzy pre-2004: raport jakości na próbce 30 aktów.
- **R5. Terminologia PL vs UE w retrievalu** („zgoda na przetwarzanie" vs język rozporządzenia).
  Ta sama klasa porażki co w `RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md`. Najbardziej prawdopodobna
  przyczyna, jeśli metryka nie drgnie po T1.
- **R6. Mieszanie porządków prawnych** — polski przepis w odpowiedzi na pytanie unijne i odwrotnie.
  W pomiarze „po" patrzymy, CO model cytuje, nie tylko czy odpowiedział.
- **R7. Rozmycie retrievalu polskiego** przez 6 750 nowych aktów (część bardzo technicznych).
  Mitygacja: przebieg zestawu egzaminacyjnego PL po T1 — regresja na pytaniach polskich jest
  warunkiem zatrzymania T2/T3.
- **R8. Zmiana schematu markupu CELLAR-a** (konwerter wersjonowany w komentarzu HTML:
  widziane `9.16.1` i `9.18.0`). Mitygacja: `QualityIssues`, gdy 0 artykułów albo nierozpoznany wariant.
- **R9. Regresja `CitationParser`** przy dokładaniu `ust./lit.` — testy równoważności przed zmianą.
