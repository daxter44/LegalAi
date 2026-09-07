# Przypadek: kwalifikacja budowla/budynek (u.p.o.l.) — dwie odmowy, dwie różne przyczyny

Data: 2026-07-23. Realna rozmowa użytkownika (obie tury ocenione „zła odpowiedź"), zdiagnozowana
i zweryfikowana bezpośrednio na żywej bazie + kodzie. Format: **[FAKT]** = zmierzone/sprawdzone,
**[HIPOTEZA]** = nie zbadane, otwarte pytanie.

## Pytanie i odpowiedzi (transkrypt, skrócony do treści merytorycznej)

**Tura 1**: „jak po 1 stycznia 2025 r. kwalifikować obiekty do podatku od nieruchomości — budowla
czy budynek?"

Model podał schemat kwalifikacji (obiekt budowlany → nie obiekt małej architektury → cechy budynku
→ budowla per wykluczenie), cytując wyłącznie orzecznictwo z lat **2010-2022** ([2] I SA/Po 594/17
2017, [3] II FSK 1932/09 2010, [4] I SA/Po 89/15 2015, [6][7][8] podobnie stare). Zakończył:

> „Źródła nie zawierają informacji o zmianach w definicjach budynku lub budowli dla celów podatku
> od nieruchomości, które wchodziłyby w życie 1 stycznia 2025 r."

Jedyne trafione źródło z 2025 r. ([1], `DU/2025/1817`) dotyczy amortyzacji PIT/CIT — inny temat,
przypadkowe trafienie leksykalne na „budynki niemieszkalne i budowle".

**Tura 2** (dopytanie): „a Art. 1a USTAWA O PODATKACH I OPŁATACH LOKALNYCH ?"

Model, mimo jawnie podanego numeru artykułu i pełnej nazwy ustawy:

> „Źródła nie obejmują pełnej treści brzmienia art. 1a ustawy o podatkach i opłatach lokalnych."

## [FAKT] Oba stwierdzenia „źródła nie zawierają" są nieprawdziwe

Sprawdzone bezpośrednio na zdalnej bazie (192.168.100.11):

```sql
SELECT "ExternalId", "Title" FROM documents WHERE "Title" ILIKE '%podatkach i opłatach lokalnych%';
-- DU/1991/31   Ustawa z dnia 12 stycznia 1991 r. o podatkach i opłatach lokalnych.  (JEST)
-- DU/2024/1757 Ustawa z dnia 19 listopada 2024 r. o zmianie ustawy o podatku rolnym,
--              ustawy o podatkach i opłatach lokalnych oraz ustawy o opłacie skarbowej  (JEST)
```

- `DU/1991/31`, art. 1a — chunk w bazie zawiera **AKTUALNĄ** definicję: „budynek – obiekt wzniesiony
  w wyniku robót budowlanych, wraz z instalacjami zapewniającymi możliwość jego użytko[wania]…" —
  inne sformułowanie niż stare orzecznictwo (które odsyłało do Prawa budowlanego, nie definiowało
  autonomicznie).
- `DU/2024/1757` — dokładnie ta nowela, o którą pytał użytkownik: chunki zawierają zmiany definicji
  „obiektu budowlanego", „urządzenia technicznego", „fundamentów pod maszyny" w art. 1a/2 u.p.o.l.

Wniosek: treść odpowiadająca na pytanie użytkownika **jest** w korpusie. To nie luka źródłowa
(jak wcześniej RODO czy WSA/NSA) — to porażka retrievalu, i to DWIE różne, niezależne przyczyny.

## [FAKT — zweryfikowane w kodzie] Przyczyna Tury 2: `CitationParser.ActHint()` nie zna nazw ustaw

`src/PrawoRAG.Domain/Retrieval/CitationParser.cs:61-68`:

```csharp
private static string? ActHint(string text)
{
    var k = KodeksRe.Match(text);
    if (k.Success) return Ws.Replace(k.Value, " ").Trim();
    foreach (var (norm, re) in Abbrevs)
        if (re.IsMatch(text)) return norm;
    return null;
}
```

Rozpoznaje WYŁĄCZNIE: frazy „kodeks…" (`KodeksRe`) i 11 zaszytych skrótów
(`KPC, KPK, KKW, KKS, KRO, KPA, KSH, KK, KC, KW, KP`). „USTAWA O PODATKACH I OPŁATACH LOKALNYCH"
nie pasuje do żadnego wzorca → `ActHint` zwraca `null` → tor strukturalny (QU-3, dokładne
dociągnięcie artykułu) **nigdy się nie uruchamia**, mimo że użytkownik podał numer artykułu I pełną
nazwę ustawy — najbardziej jednoznaczny możliwy sygnał.

To dotyczy KAŻDEJ ustawy, która nie jest „kodeksem" ani jednym z 11 skrótów: Prawo budowlane,
Ordynacja podatkowa, ustawa o VAT, o podatku rolnym, o CIT/PIT, o samorządzie gminnym itd. —
potencjalnie systemowa luka w rozpoznawaniu cytatów, nie odosobniony przypadek. **Niezbadane**:
jaki odsetek realnych pytań użytkowników cytuje ustawy spoza tej listy 11 skrótów.

## [FAKT — zmierzone sondą] Przyczyna Tury 1: trafienie ginie w fuzji RRF, nie w samym retrievalu

Sonda `--probe-chunk` dla chunka art. 1a zawierającego definicję budynku (436 tok.):

```
A. exact fp32:   #533    (sim=0,8541)
C. HNSW (ef=400): NIEOBECNY w top-200
D. BM25:          #30    tsquery MATCHUJE chunk
E. fuzja RRF:     dense@50: brak, sparse@50: #30, pula RRF: #60/86 → ODPADA
                  (pula do dedupu = TopK×4 = 32 → cutoff PRZED dedupem)
```

BM25 znalazł ten chunk (pozycja #30 z 50 kandydatów sparse) — to zadziałało. Po fuzji RRF z torem
gęstym (który go nie widział wcale, dense rank #533) połączony ranking spycha go na **#60** —
**28 miejsc za progiem** `TopK×4=32`, na którym pula jest przycinana przed dedupem/rerankerem.

## [FAKT — zmierzone `Eval__ProbeDumpTop=true`] Co faktycznie zajmuje top-40 dense dla tego pytania

Pytanie: „a Art. 1a USTAWA O PODATKACH I OPŁATACH LOKALNYCH ?". Poniżej PEŁNA lista 40 pozycji
zwróconych przez sondę (tytuł aktu + oznaczenie jednostki + podgląd treści), bez selekcji.

**Zliczenia (policzone bezpośrednio z tych 40 pozycji, bez interpretacji):**

| kategoria | liczba / 40 | pozycje |
|---|---|---|
| DocType = `act` | 40/40 | wszystkie |
| DocType = orzeczenie (judgment) | 0/40 | — |
| Oznaczone dosłownie „(wariant N/M)" w etykiecie jednostki | 9/40 | #13, #15, #18, #19, #22, #23, #27, #28, #38 |
| Oznaczone „(pominięty)" lub „(uchylony)" | 3/40 | #2, #5, #29 |
| Podgląd treści = pojedynczy punkt listy zaczynający się od cyfry+nawiasu (np. „1) zapłaty,") | 15/40 | #8, #9, #12, #13, #15, #18, #19, #26, #27, #28, #31, #32, #33, #37, #38 |
| Dotyczące bezpośrednio `DU/1991/31` (u.p.o.l.) lub jego nowelizacji „o zmianie ustawy o podatkach i opłatach lokalnych" | 4/40 | #2, #6, #21, #34 |
| Z tych 4 — zawierające art. 1a (szukaną jednostkę) | 0/40 | — |

**Przykłady dosłowne (kopiowane z wyjścia sondy):**

- `#2 [act] Ustawa z dnia 12 stycznia 1991 r. o podatkach i opłatac… Art. 22 — o podatkach i opłatach lokalnych, Art. 22 (pominięty)`
  — ten sam akt co szukany (u.p.o.l.), ale inny artykuł, oznaczony jako pominięty; ranga #2 w dense.
- `#5 [act] Ustawa z dnia 27 sierpnia 2009 r. o finansach publiczny… Art. 112a — o finansach publicznych, Art. 112a (uchylony) Art. 112a1. (uchylony)`
- `#12 [act] Ustawa z dnia 12 września 2002 r. o zmianie ustawy - Or… Art. 59 § 1 pkt 1 — 1) zapłaty,`
- `#13 [act] Ustawa z dnia 11 kwietnia 2001 r. o zmianie ustawy - Or… Art. 1 § 2 pkt 1 (wariant 1/2) — 1) ustalająca wysokość zobowiąza…`
- `#18 [act] Ustawa z dnia 12 września 2002 r. o zmianie ustawy - Or… Art. 1 § 1 pkt 1 (wariant 23/32) — 1) organu podatkowego wyższe…`
- `#21 [act] Ustawa z dnia 12 stycznia 1991 r. o podatkach i opłatac… Art. 23 — Traci moc ustawa z dnia 14 marca 1985 r. o podatkach i opłatach lokalnych (Dz. U. poz. 50, z 1988 …`
- `#34 [act] Ustawa z dnia 30 października 2002 r. o zmianie ustawy … Art. 1b — Ulgi i zwolnienia podatkowe w zak…`

**Pełna lista 40 pozycji (tytuł skrócony + jednostka), w kolejności dense:**

```
#1  CIT (1992), Art. 1a                         #21 u.p.o.l. (1991), Art. 23 (Traci moc...)
#2  u.p.o.l. (1991), Art. 22 (pominięty)         #22 planowanie i zagosp. przestrz., Art. 67c (wariant 1/2)
#3  PIT (1991), Art. 31a                         #23 nowela Ordynacja podatkowa (2026), Art. 1 § 1 (wariant 2/2)
#4  o KAS (2016), Art. 49aaa                     #24 nowela CIT/pod. instytucji fin. (2025), Art. 1
#5  o finansach publ. (2009), Art. 112a (uchylony) #25 CIT (1992), Art. 11c
#6  nowela u.p.o.l. (2002), Art. 1c              #26 nowela Ordynacja podatkowa (2001), Art. 35 § 2 pkt 1
#7  CIT (1992), Art. 17a                         #27 nowela Ordynacja podatkowa (2002), Art. 1 § 2 pkt 1 (wariant 25/33)
#8  nowela Ordynacja podatkowa (2015), Art. 1 § 1a pkt 1  #28 nowela Ordynacja podatkowa (2002), Art. 1 § 2 pkt 1 (wariant 6/33)
#9  nowela Ordynacja podatkowa (2008), Art. 1 § 4 pkt 1   #29 o regionalnych izbach obrach. (1992), Art. 28 (pominięty)
#10 CIT (1992), Art. 4a                          #30 nowela KAS/VAT (2025), Art. 1
#11 CIT (1992), Art. 15a                         #31 nowela Ordynacja podatkowa (2002), Art. 59 § 2 pkt 1
#12 nowela Ordynacja podatkowa (2002), Art. 59 § 1 pkt 1  #32 nowela Kodeks karny skarbowy (2005), Art. 1 § 39 pkt 1
#13 nowela Ordynacja podatkowa (2001), Art. 1 § 2 pkt 1 (wariant 1/2) #33 nowela Kodeks karny (2022), Art. 1 § 1 pkt 1
#14 CIT (1992), Art. 11q                         #34 nowela u.p.o.l. (2002), Art. 1b
#15 nowela Ordynacja podatkowa (2002), Art. 1 § 1 pkt 1 (wariant 8/32) #35 nowela planowania przestrz. (2026), Art. 1
#16 PIT (1991), Art. 23a                         #36 CIT (1992), Art. 18db
#17 PIT (1991), Art. 10                          #37 nowela Ordynacja podatkowa (2002), Art. 103 § 1 pkt 4
#18 nowela Ordynacja podatkowa (2002), Art. 1 § 1 pkt 1 (wariant 23/32) #38 nowela Ordynacja podatkowa (2002), Art. 1 § 1 pkt 1 (wariant 16/32)
#19 nowela Ordynacja podatkowa (2002), Art. 1 § 1 pkt 1 (wariant 5/32) #39 PIT (1991), Art. 26ea
#20 nowela Ordynacja podatkowa (2026), Art. 18   #40 o zasadach ewidencji podatników (1995), Art. 15aa
```

Chunk z faktyczną definicją budynku/budowli (art. 1a u.p.o.l., cel sondy) NIE występuje w tej
liście 40 pozycji — jego zmierzona dokładna ranga to #533 (sekcja wyżej).

Pozostałe dwa chunki art. 1a (dalsze ustępy — „kondygnacja", odesłania do Dziennika UE) mają dense
rank w dziesiątkach/setkach tysięcy i zero dopasowania BM25 — to inne podtematy tego samego
artykułu, ich niska pozycja wygląda na POPRAWNĄ (nie dotyczą wprost pytania), nie na błąd.

**To ten sam mechanizm co wcześniej opisany w `RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md`**
(przypadek: „dense@50 → #33, pula do dedupu = 32 → ODPADA, jedno miejsce ZA odcięciem"), gdzie
**poszerzenie puli `TopK×4` zostało jawnie odrzucone jako rozwiązanie** — bez zbadania, co realnie
konkuruje w tych ~60 miejscach (śmieci czy trafna treść), podniesienie liczby to zgadywanie, nie
naprawa. Ten dokument świadomie NIE proponuje takiej zmiany z tego samego powodu.

## [FAKT — zmierzone `Eval__ProbeDumpFused=true`, narzędzie CIT-2] Pełny zfuzowany ranking top-60

Ten sam dzień, po zaciągnięciu commitów `757f20e` (CIT-1: `CitationParser.ActHint` rozpoznaje pełne
nazwy ustaw korpusowo) i `9c8343f` (CIT-2: `ChunkClassifier` + `Eval__ProbeDumpFused`) z równoległej
sesji. Sonda odtworzona identycznym pytaniem, teraz z klasyfikacją automatem zamiast liczeniem ręcznym.

**Cel sondy (art. 1a, definicja budynku) ląduje dokładnie na pozycji #60** tego dumpu — potwierdzone
tą samą sondą, spójne z wcześniejszym pomiarem „pula RRF: #60/86".

**Rozkład klas na 60 pozycjach (wydruk narzędzia):**

| klasa | liczba/60 |
|---|---|
| orzeczenie (judgment) | 28 |
| akt-bazowy (BaseAct) | 15 |
| wariant (AmendmentVariant) | 9 |
| nowela (AmendmentAct) | 8 |
| uchylony/pominięty (RepealedOrOmitted) | 0 |
| cienkie (≤40 tok.) | 27 |
| cienkie + punkt wyliczenia (ThinEnumeration) | 9 |
| punkt wyliczenia (dowolnej długości) | 11 |

**Rozbicie regionu #33-59 (27 pozycji między odcięciem `TopK×4=32` a celem na #60) — policzone
bezpośrednio z wypisanych pozycji:**

| klasa | liczba/27 |
|---|---|
| orzeczenie | 13 |
| akt-bazowy (inny artykuł) | 4 |
| wariant | 6 |
| nowela | 4 |

**Obserwacja bezpośrednia, bez zaokrągleń**: `uchylony/pominięty` = 0/60 — pozycje tego typu widoczne
wcześniej w SAMYM dense top-40 (np. „Art. 22 (pominięty)" na #2 dense) nie występują w zfuzowanym
top-60 wcale. Ranking BM25, wchodzący do fuzji, ich tam nie stawia.

**Arytmetyka pod decyzję CIT-3/CIT-4 (bezpośrednio z powyższych liczb, nie osobny pomiar)**:
- Usunięcie WSZYSTKICH 6 wariantów z regionu #33-59 przesunęłoby cel z #60 najwyżej na #54 — nadal
  22 pozycje za cutoffem #32. Sam dedup wariantów (zakres CIT-3) nie wystarcza, żeby ten konkretny
  cel przekroczył próg.
- `cienkie+wyliczenie` (zakres CIT-4) to tylko 9/60 na całej liście — mniejsza kategoria niż liczba
  wariantów+noweli razem (17/60).
- Największa pojedyncza kategoria w całości (28/60) i w regionie blokującym (13/27) to `orzeczenie`
  — poza zakresem zarówno CIT-3, jak i CIT-4 tak jak są dziś nazwane/opisane w kodzie.

## Otwarte pytania (nie zbadane w tej sesji)

1. ~~Co dokładnie zajmuje pozycje #1-59 w fuzji RRF dla tego pytania~~ — **ZMIERZONE w pełni**
   (sekcja „Pełny zfuzowany ranking top-60" wyżej, narzędzie CIT-2). Niezbadane pozostaje, czy ten
   sam rozkład klas (dominacja orzeczeń, ~15% wariantów+noweli) powtarza się na INNYCH pytaniach,
   czy to specyficzne dla tego jednego przypadku — jeden zmierzony ranking nie ustala wzorca.
1a. ~~Czy CIT-1 (rozpoznawanie pełnych nazw ustaw) naprawia Turę 2~~ — kod scalony, ale NIE
   zweryfikowane na żywo (czy fuzzy resolver po `ActHint="podatkach i opłatach lokalnych"`
   faktycznie trafia w `DU/1991/31` — `ResolveActAsync`/pg_trgm nie zostały tu przetestowane end-to-end).
2. Jaki odsetek pytań o konkretny artykuł konkretnej (nie-kodeksowej) ustawy cierpi na ten sam brak
   rozpoznania w `ActHint()` — pojedynczy zmierzony przypadek nie mówi nic o skali.
3. Czy naprawa `ActHint()` (rozszerzenie o pełne nazwy ustaw) powinna być słownikiem popularnych
   ustaw, dopasowaniem po tytule dokumentu w bazie, czy fuzzy-matching — nierozstrzygnięte, wymaga
   decyzji o podejściu i pomiaru, nie zgadywania.

## Powiązane

- [POMIARY-TOPK-CANDIDATESPERPATH-HNSW-2026-07-23.md](POMIARY-TOPK-CANDIDATESPERPATH-HNSW-2026-07-23.md) —
  ten sam dzień, analogiczny mechanizm (art. 56 KRO, art. 41 KPK) na innych pytaniach.
- [PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md](PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md) — analogiczny
  wzorzec „jawny identyfikator podany przez użytkownika, a system go nie rozpoznaje", tam już
  naprawiony dla sygnatur orzeczeń (`CaseNumberKey`) i numerów Dziennika Ustaw (`ActEliKey`);
  `CitationParser.ActHint()` to TRZECI, osobny przypadek tej samej klasy problemu (nazwa ustawy),
  nienaprawiony.
- [RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md](RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md) —
  pierwotny opis mechanizmu „ODPADA przed dedupem" i odrzucenia poszerzenia `TopK×4` bez badania.
