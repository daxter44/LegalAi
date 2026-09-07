# Analiza z pomiarami: wchłonięte nowelizacje w retrievalu (kontynuacja diagnozy „nadgodziny")

Data: 2026-09-01. Kontynuacja: `DIAGNOZA-NADGODZINY-PRZESTARZALA-TRESC-NOWELI-2026-09-01.md`.
**Zakres: wyłącznie analiza i pomiar — żadna zmiana w kodzie, promptach ani bazie nie została
wprowadzona.** Wszystkie zapytania read-only na produkcyjnej bazie M4; narzędzia pomiarowe leżą
poza repo (scratchpad sesji, sekcja „Narzędzia").

## TL;DR

1. Problem jest **systemowy, nie n=1**: chunki ustaw nowelizujących to **31% całego toru aktów**
   (421 tys. z 1,34 mln chunków), a w top-50 wektorowym typowych pytań stanowią **18–62%** wyników.
2. Filtr „bez wchłoniętych nowel" (kierunek (a) z diagnozy) **usuwa złe źródła, ale sam nie
   wciąga właściwego przepisu do puli**: art. 151¹ §1 KP awansuje z pozycji 269 na 138 — nadal
   daleko poza pulą (50/tor). Ratunkiem jest to, że po filtrze **art. 151¹ §2 KP wchodzi na #9**
   — a istniejące sąsiedztwo artykułów (plan SAS) dociągnęłoby wtedy §1 z poprawnymi stawkami.
   To wymaga potwierdzenia pełną symulacją RRF+BM25, nie tylko torem wektorowym.
3. Wcześniejsze podejrzenie, że listy `unabsorbedAmendments` są puste, było **błędem pomiaru**
   (zły klucz JSON `id` vs `EliId`) — w rzeczywistości 653 akty bazowe mają niepuste listy,
   KPC nadal wskazuje DU/2026/473. Strażnik świeżości (AKT) jest nietknięty.

## Pomiary

### Skala zjawiska w korpusie (stan 2026-09-01, po reprocessie ustępów)

| Miara | Wartość |
|---|---|
| Chunki w torze aktów (`DocType='act'`) | 1 344 338 |
| Akty „o zmianie…" w korpusie | 2 488 z 17 378 aktów (14%) |
| Ich chunki | 437 644 (**33% toru aktów**) |
| Z tego nowele NIEWCHŁONIĘTE (na listach `unabsorbedAmendments`) | 194 akty |
| Nowele WCHŁONIĘTE (treść żyje już w tekstach jednolitych) | 2 294 akty / **421 130 chunków (31% toru)** |
| Akty bazowe z niepustą listą `unabsorbedAmendments` | 653 (892 unikalne ELI nowel) |

Uwaga metodyczna: klucz elementów listy to `EliId`, nie `id` — kwerenda z `->>'id'` zwraca puste
wartości i fałszywie sugeruje brak niewchłoniętych nowel.

### Pytanie-nośnik: „Jakie wynagrodzenie przysługuje mi za nadgodziny?"

Ranking exact fp32 (cosine, tor aktów), embedding zapytania przez produkcyjny TEI
(prefiks `zapytanie: `, normalizacja jak w retrieverze):

| Wariant | Rank art. 151¹ §1 KP (`DU/1974/141`) |
|---|---|
| baseline (dzisiejszy stan) | **269** |
| po odfiltrowaniu chunków aktów „o zmianie…" | **138** |

(Diagnoza podawała #443 — tam ranking liczony był na innym zbiorze/momencie; kierunek i rząd
wielkości się zgadzają: przepis jest głęboko poza pulą 50/tor w obu pomiarach.)

Top-20 baseline: **8 z 20 pozycji to chunki nowel** (w tym #2, #3, #4 — kolejne historyczne wersje
przepisów o nadgodzinach z `DU/2001/1405`, `DU/2003/2081`, `DU/2002/1146`, `DU/1996/110`).
Po filtrze czołówka to przepisy merytoryczne, a do top-10 wchodzą dwa chunki Kodeksu pracy:
**art. 151¹ §2 (#9)** i art. 77⁵ §2 (#12). To ważne: §2 w puli + rozszerzenie sąsiedztwa (SAS)
= §1 z właściwymi stawkami trafia do kontekstu jako sąsiad.

### Kontaminacja top-50 chunkami nowel — bateria pytań (skala poza n=1)

| Pytanie | Udział chunków nowel w top-50 |
|---|---|
| Jaki jest okres wypowiedzenia umowy o pracę? | **62%** |
| Ile dni urlopu wypoczynkowego przysługuje pracownikowi? | **52%** |
| Kiedy przedawniają się roszczenia o zaległe wynagrodzenie? | 42% |
| Ile wynosi zasiłek pogrzebowy? | 38% |
| Ile wynosi zachowek po rodzicach? | 28% |
| Jaka jest wysokość odprawy przy zwolnieniach grupowych? | 18% |

Wniosek: dla pytań o często nowelizowane przepisy (prawo pracy!) nawet ~2/3 puli wektorowej to
treść nowelizacji — w ogromnej większości wchłoniętych, czyli przestarzałych merytorycznie.

## Ryzyka i pułapki zmierzonego kierunku (a) — filtr retrievalu

1. **Przepisy przejściowe i autonomiczne w nowelach.** Ustawa nowelizująca to nie tylko payload
   zmian: np. `DU/2003/2081` ma poza art. 1 własne art. 10, 108a–108c (real­na treść normatywna,
   której NIE ma w żadnym tekście jednolitym). Twarde wykluczenie całych aktów „o zmianie…"
   usuwa je z toru wektorowego. Nie da się ich odsiać po locatorze („warianty" to mechanizm
   rozróżniania duplikatów jednostek, nie marker payloadu zmian).
2. **Heurystyka tytułu ma fałszywe trafienia.** ~50 z 2 488 aktów „o zmianie…" to nie klasyczne
   nowele ustaw (np. „o zmianie nazw szkół wyższych", „o zmianie zakresu obowiązywania Konwencji…",
   „o zmianie Konstytucji"). Autorytatywny sygnał („Akty zmienione" z metadanych ELI) nie jest dziś
   zapisywany w `TypedMetadata` — status ISAP też nie pomaga (wchłonięte nowele mają u nas status
   „obowiązujący", nie „akt objęty tekstem jednolitym").
3. **Nowele niewchłonięte muszą zostać w grze** (194 akty) — na nich stoi świeżość (golden set
   Freshness, marker `[NOWELIZACJA …]`); augmenter dostarcza je niezależnie od retrievalu, ale
   organiczne trafienia też się liczyły w pomiarach AKT.

## Opcje naprawy (ŻADNA niewdrożona — do decyzji)

- **(a) Filtr/degradacja wchłoniętych nowel w retrievalu.** Zbiór: `DocType='act'` + tytuł
  nowelizacyjny + ELI nieobecne na żadnej liście `unabsorbedAmendments`. Wykonanie: flaga
  materializowana per dokument (backfill jednym UPDATE, bez reembeddingu; utrzymywana w relinku
  AKT-5.2, bo nowela z czasem zmienia stan wchłonięcia w obie strony przy nowym t.j.).
  Wariant twardy (WHERE) vs miękki (kara do wyniku RRF — zachowuje przepisy przejściowe w grze,
  gdy nie ma lepszych źródeł). Zmierzony efekt: czyści pulę (top-20 bez wersji historycznych),
  podnosi właściwy akt 2×, ale finalny sukces zależy od współpracy z sąsiedztwem.
- **(b) Reguła syntezy w promptcie.** Gdy pula zawiera kilka wersji tego samego przepisu
  (ten sam artykuł, różne akty/lata), cytować liczby/stawki z NAJNOWSZEGO źródła albo z tekstu
  jednolitego. Tanie, niezależne od (a); łata drugi zmierzony w diagnozie problem (model wybrał
  wersję z 1996 r., mając poprawną z 2003 r. w puli). Nie naprawia retrievalu.
- **(c) Zmiana chunkingu nowel** (nie tworzyć krótkich, czystych chunków payloadu) — wymaga
  reprocessingu ~2,5 tys. aktów; najdroższa, odkładana.
- **(d) Nic w retrievalu, licz na sąsiedztwo** — zmierzone: bez filtra §2 KP jest na #19 w torze
  exact i w realnej odpowiedzi NIE wszedł do puli; samo sąsiedztwo nie wystarcza.

## Proponowana bramka przed jakimkolwiek wdrożeniem (gdy zapadnie decyzja)

1. Pełna symulacja puli RRF (wektor+BM25, ChunkProbe) dla pytania-nośnika z flagą filtra —
   potwierdzić, że §2/§1 KP faktycznie wchodzą do finalnej puli.
2. Golden set PRZED/PO — kill-condition: spadek FreshnessRecall (strażnik AKT) lub trafień
   ogółem; osobno obejrzeć pytania, gdzie źródłem poprawnej odpowiedzi była nowela.
3. Pytanie-nośnik na żywym czacie: odpowiedź ma cytować art. 151¹ §1 KP (100%/50%), nie stawki
   z 1996 r.

## Narzędzia (poza repo, scratchpad sesji 2b94e3ca)

- `sqlq` — ad-hoc SELECT-y (Npgsql) na bazie M4;
- `nadprobe` — embedding pytania przez TEI M4 + ranking exact fp32 toru aktów z/bez symulowanego
  filtra + kontaminacja top-50 dla baterii pytań. Oba read-only.
