# Diagnoza: pewna, błędna odpowiedź o skardze na jednostkę budżetową — 2026-09-02

Rozmowa `01a061bf-bd92-7dd1-b246-340eb90f553f` (2026-09-02 10:52).
Kontrola: `01a061cc-25a4-791a-9009-a38497d4f8d9` (11:06).

## Skutek: użytkownik dostał odpowiedź prawnie ODWROTNĄ

Pytanie: *„Czy mogę złożyć skargę na działanie jednostki budżetowej? Jakie formalności muszę
spełnić? Czy mogę złożyć ją anonimowo?"*

System odpowiedział: **„zgłoszenie może być złożone anonimowo [4]"** — cytując ustawę o ochronie
sygnalistów, art. 7 ust. 1.

Stan prawny dla skargi z Działu VIII KPA: **§ 8 ust. 1 rozporządzenia RM z 8.01.2002** —
*„Skargi i wnioski niezawierające imienia i nazwiska (nazwy) oraz adresu wnoszącego pozostawia się
bez rozpoznania."*

`Abstained=f`, `CitationClean=t`, `Regenerated=f`. **Wszystkie bramki zadziałały zgodnie
z projektem.** Cytat był prawdziwy — tylko z niewłaściwego aktu. Gap-closing nie odpalił się
poprawnie: dystanse 0.19–0.21, similarity ~0.80, próg `GapClosingTriggerThreshold=0.55`.

To klasa „confident but wrong" — żadne istniejące zabezpieczenie jej nie łapie z definicji.

Poboczne w tej samej rozmowie: tura 3 zwróciła **pustą odpowiedź** (0 znaków przy 14 źródłach,
`Abstained=f`) — osobny wątek, por. `bacf8cd`.

## Eksperyment kontrolny (zadany przez użytkownika, nieświadomie)

To samo pytanie 14 minut później, zmieniona JEDNA fraza:

| Fraza | Źródła w produkcji | Wynik |
|---|---|---|
| „jednostki **budżetowej**" | sygnalista 34, KPA 0, rozporządzenie 0 | **błędny** |
| „jednostki **samorządu terytorialnego**" | KPA 11, sygnalista 15, rozporządzenie 1 | poprawny |

„Samorządu terytorialnego" to fraza dosłownie obecna w KPA art. 221 § 2 / 223 § 1.
Użytkownik przypadkiem podał kotwicę. „Jednostka budżetowa" to termin z ustawy o finansach
publicznych — w KPA nie występuje ani razu.

## To nie jest luka w korpusie

KPA (`DU/1960/168`, Indexed, InForce, 670 chunków) ma cały Dział VIII pochunkowany per §:
art. 221, 222, 223, 226, 227, 228, 229, 237, 238, 253. Rozporządzenie RM 8.01.2002 — 14 chunków.
Oba obecne. Retrieval ich nie znalazł.

## Metoda pomiaru

TEI `.11:8080` + prefiks `zapytanie: `, surowe SQL jak `HybridRetriever.DenseAsync`
(`ef_search=1000`, `TokenCount>=20`, filtry AbsorbedAmendment / judgmentType), top-50.
**Tylko tor gęsty** — bez BM25, rerankera i sąsiedztwa. Walidacja przybliżenia: wariant kontrolny
odtwarza produkcję (KPA 5 tu vs 11 tam), a wariant surowy odtwarza porażkę dokładnie.

## Wyniki — trzy prawdopodobne naprawy, wszystkie OBALONE

| # | Wariant | KPA | rozporz. | sygnalista |
|---|---|---|---|---|
| A | surowe pytanie (stan dzisiejszy) | 0 | 0 | 12 |
| B | rewrite z loga diagnostycznego (*nazywa KPA*) | 14 | 1 | 4 |
| B' | rewrite **odtworzony** z gemma4:26b-mlx (*nie nazywa aktu*) | 0 | 0 | **18** |
| C | kontrola „samorządu terytorialnego" | 5 | 1 | 7 |
| D | surowe + doklejone „Kodeks postępowania administracyjnego" | 16 | 1 | 5 |
| E | „budżetowej" → „organu administracji publicznej" | 4 | 2 | 6 |
| F | surowe bez słowa „anonimowo" | 0 | 0 | **0** |

**Obalone 1 — `RouteDecision.Zapytanie` z routera nie jest rozwiązaniem.**
Router odtworzony na gemma4:26b-mlx (temp=0, ten sam prompt) tworzy poprawny stylistycznie rewrite,
który NIE nazywa aktu — i jest **gorszy niż nic** (sygnalista 12→18, KPA dalej 0). Cały efekt
w wariancie B robił jeden element: nazwa aktu. Wariant D izoluje to czysto.

**Obalone 2 — sklejone spacje nie są przyczyną w torze gęstym.**
Rozporządzenie ma rozwalony tekst („iwnioski", „ztreści", „pozostawiasię"), `QualityIssues={}`,
przetworzone 2026-07-12, przed poprawką z 08-28. Ręcznie poprawiony § 8 wypada **GORZEJ**:
0.2581 vs 0.2490 (pytanie surowe), 0.2355 vs 0.2260 (pytanie o anonimowość).
Dla BM25 sklejki nadal szkodzą — to osobny tor i osobna sprawa.

**Obalone 3 — zakotwiczenie w akcie nie ratuje rozporządzenia, tylko je wypycha.**
W wariancie D fragmenty rozporządzenia wypadają z czołówki na rzecz KPA. A to § 8 rozporządzenia,
nie KPA, odpowiada na pytanie o formalności i anonimowość.

## Przyczyna właściwa: § 8 jest nieosiągalny semantycznie

§ 8 **nie wszedł do top-50 w ŻADNYM wariancie**, łącznie z pytaniem zadanym maksymalnie wprost
o anonimowość (dystans 0.2260 przy czołówce puli ~0.13–0.21).

Dwie niezależne bariery:

1. **Zerowe pokrycie słownikowe.** Słowo „anonim\*": rozporządzenie **0/14** chunków, KPA **0**,
   ustawa o sygnalistach **7/320**. § 8 opisuje anonimowość definicją („niezawierające imienia
   i nazwiska (nazwy) oraz adresu"), nigdy nazwą. Jedyny leksykalny dom słowa użytego przez
   użytkownika leży w niewłaściwej ustawie — i tam trafia zapytanie.
   Wariant F potwierdza kierunek: usunięcie „anonimowo" zeruje sygnalistę (12→0), ale **nie**
   odsłania rozporządzenia. Dwa niezależne deficyty, nie jeden.

2. **Zalanie puli boilerplate'em orzeczniczym.** Zapytanie w słownictwie samego § 8
   („Skarga niezawierająca imienia i nazwiska oraz adresu wnoszącego pozostawiona bez rozpoznania")
   daje **0 fragmentów rozporządzenia w top-50**. Pulę zajmują orzeczenia z dystansem 0.1297 —
   ta sama wartość dla pięciu różnych sądów, bo to **szablon „Formularz UK 2"**
   („☐ zasadny ☒ częściowo zasadny", listy art. 438 k.p.k.). To nie jest merytoryczne
   orzecznictwo wygrywające z ustawą — to duplikat formularza. Problem węższy i łatwiejszy
   niż „mamy 519 677 orzeczeń vs 17 339 ustaw".

## Dlaczego tura 2 „zadziałała"

Użytkownik nazwał rozporządzenie tytułem i datą — to idzie torem dokładnym (tytuł/ELI), nie
semantycznym. Rozporządzenie było więc znajdowalne wyłącznie wtedy, gdy użytkownik już wiedział,
czego szuka. Dokładnie odwrotnie, niż wymaga tego produkt.

## Powiązania

- `project_term_mismatch_retrieval_pattern` — czwarty udokumentowany przypadek, pierwszy
  z dopasowaną kontrolą.
- `project_statute_not_retrievable_nl_questions` — bariera 2 to świeży, mocny dowód na potrzebę
  toru act-only.
- `project_gapclosing_threshold_decoupled` — potwierdza, że „confident but wrong" pozostaje otwarte.
