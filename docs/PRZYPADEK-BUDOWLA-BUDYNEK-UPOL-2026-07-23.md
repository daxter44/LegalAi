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

Pozostałe dwa chunki art. 1a (dalsze ustępy — „kondygnacja", odesłania do Dziennika UE) mają dense
rank w dziesiątkach/setkach tysięcy i zero dopasowania BM25 — to inne podtematy tego samego
artykułu, ich niska pozycja wygląda na POPRAWNĄ (nie dotyczą wprost pytania), nie na błąd.

**To ten sam mechanizm co wcześniej opisany w `RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md`**
(przypadek: „dense@50 → #33, pula do dedupu = 32 → ODPADA, jedno miejsce ZA odcięciem"), gdzie
**poszerzenie puli `TopK×4` zostało jawnie odrzucone jako rozwiązanie** — bez zbadania, co realnie
konkuruje w tych ~60 miejscach (śmieci czy trafna treść), podniesienie liczby to zgadywanie, nie
naprawa. Ten dokument świadomie NIE proponuje takiej zmiany z tego samego powodu.

## Otwarte pytania (nie zbadane w tej sesji)

1. Co dokładnie zajmuje pozycje #1-59 w fuzji RRF dla tego pytania — realna konkurencyjna treść,
   czy szum? (`Eval__ProbeDumpTop=true` pozwala to zobaczyć, jak w JAK-3 wcześniej.)
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
