# Pomiary: TopK, CandidatesPerPath, sondy chunków (art. 41 KPK, art. 56 KRO)

Data: 2026-07-23. Zebrane dowody z serii pomiarów uruchomionych na żądanie „czy TopK=8 to za
mało?". Dokument przedstawia SUROWE wyniki i bezpośrednie obserwacje z nich wynikające — bez
wniosków o przyczynie ani rekomendacji naprawy (to osobna decyzja, do podjęcia na podstawie tych
danych, nie w tym dokumencie).

## Test 1: golden-set (16 pozycji) przy TopK=8 vs TopK=21

Uruchomienia: `dotnet run -c Release --project src/PrawoRAG.Eval` (bez flagi = tryb golden-set,
`Eval:Chat=false`), z `Retrieval__TopK=8` (domyślne) i `Retrieval__TopK=21` — wszystko inne
identyczne (ta sama zdalna baza 192.168.100.11, ten sam `golden-set.json`, 16 pozycji).

### Wynik zagregowany — identyczny w obu przebiegach

| metryka | TopK=8 | TopK=21 |
|---|---|---|
| Recall@K (retrieval) | 71% (5/7) | 71% (5/7) |
| Trafność abstynencji | 56% | 56% |
| Śr. similarity w korpusie / poza | 0,847 / 0,856 | 0,847 / 0,856 |

`MaxSimilarity` (top-1) jest z definicji niezależne od `TopK` (to zawsze najlepsze dopasowanie,
niezależnie od punktu odcięcia) — identyczność tej kolumny między przebiegami jest oczekiwana,
nie jest sama w sobie dowodem niczego. Identyczność `Recall@K` (zależnego od `TopK`) jest
obserwacją wymagającą wyjaśnienia — stąd sondy niżej.

### Wynik per pozycja (linie konsoli, obie wartości TopK, dla pozycji z oczekiwanym źródłem)

```
TopK=8  i TopK=21 — identyczne dla każdej z 7 pozycji:
[InCorpus] kk-148                sim=0,839 hit=True
[InCorpus] kpk-41                sim=0,849 hit=False
[InCorpus] kp-52                 sim=0,870 hit=True
[InCorpus] kc-415                sim=0,862 hit=True
[InCorpus] kk-278                sim=0,855 hit=True
[InCorpus] kro-rozwod            sim=0,819 hit=False
[InCorpus] konsument-odstapienie sim=0,838 hit=True
```

Dwie pozycje (`kpk-41`, `kro-rozwod`) mają `hit=False` w OBU przebiegach — czyli podniesienie
`TopK` z 8 do 21 nie zmieniło wyniku dla żadnej z 16 pozycji golden-setu.

Nagłówki konsoli potwierdzające, że override faktycznie zadziałał (nie błąd konfiguracji):
```
Golden set: 16 pozycji (…golden-set.json). Czat: False. Próg: 0,00, TopK: 8.
Golden set: 16 pozycji (…golden-set.json). Czat: False. Próg: 0,00, TopK: 21.
```

## Test 2: sonda `--probe-chunk` dla dwóch pozycji z `hit=False`

Narzędzie: `src/PrawoRAG.Eval/ChunkProbe.cs` (`--probe-chunk`). Dla wskazanego chunka (po
`Eval:ProbeEli`+`Eval:ProbeArticle`) raportuje pozycję na kolejnych etapach: A) dokładny skan
cosine fp32, B) dokładny skan fp16/halfvec, C) wyszukiwanie HNSW (ef_search=400, jak w produkcji),
D) tor BM25, E) symulację fuzji RRF + pulę po `Take(TopK×4)`.

### 2a. `kpk-41` — art. 41 KPK (wyłączenie sędziego)

Pytanie: „Kiedy sąd wyłącza sędziego od rozpoznania sprawy na wniosek strony?"

**Chunk §1** (37 tok, „Sędzia ulega wyłączeniu, jeżeli istnieje okoliczność…"):
```
A. exact fp32:   pozycja #963    (sim=0,7906)
B. exact fp16:   pozycja #964    (sim=0,7906)  (zgodne z fp32)
C. HNSW (ef=400): NIEOBECNY w top-200
D. BM25:          nieobecny w top-200; tsquery NIE matchuje chunka
E. fuzja RRF:     dense@50: brak, sparse@50: brak, pula RRF: poza pulą
```

**Chunk §2** (50 tok, „Wniosek o wyłączenie sędziego, zgłoszony na podstawie §1…"):
```
A. exact fp32:   pozycja #67     (sim=0,8147)
B. exact fp16:   pozycja #67     (sim=0,8147)  (zgodne z fp32)
C. HNSW (ef=400): pozycja #65
D. BM25:          nieobecny w top-200; tsquery NIE matchuje chunka
E. fuzja RRF:     dense@50: brak, sparse@50: brak, pula RRF: poza pulą (pula do dedupu = TopK×4 = 32 → ODPADA przed dedupem)
```

### 2b. `kro-rozwod` — art. 56 KRO (rozwód)

Pytanie: „W jakich okolicznościach sąd może orzec rozwód małżonków?"

**Chunk §1** (37 tok, „Jeżeli między małżonkami nastąpił zupełny i trwały rozkład pożycia…"):
```
A. exact fp32:   pozycja #14     (sim=0,8198)
B. exact fp16:   pozycja #14     (sim=0,8198)  (zgodne z fp32)
C. HNSW (ef=400): NIEOBECNY w top-200
D. BM25:          nieobecny w top-200; tsquery NIE matchuje chunka
E. fuzja RRF:     dense@50: brak, sparse@50: brak, pula RRF: poza pulą
```

**Chunk §2** (52 tok, „Jednakże mimo zupełnego i trwałego rozkładu pożycia rozwód nie jest dopuszczalny…"):
```
A. exact fp32:   pozycja #2899   (sim=0,7751)
B. exact fp16:   pozycja #2899   (sim=0,7751)  (zgodne z fp32)
C. HNSW (ef=400): NIEOBECNY w top-200
D. BM25:          nieobecny w top-200; tsquery NIE matchuje chunka
```

**Chunk §3** (58 tok, „Rozwód nie jest również dopuszczalny, jeżeli żąda go małżonek wyłącznie winny…"):
```
A. exact fp32:   pozycja #294    (sim=0,7973)
B. exact fp16:   pozycja #294    (sim=0,7973)  (zgodne z fp32)
C. HNSW (ef=400): NIEOBECNY w top-200
D. BM25:          nieobecny w top-200; tsquery NIE matchuje chunka
```

## Obserwacje bezpośrednie (wyłącznie to, co dane pokazują)

1. We wszystkich 5 zbadanych chunkach (2× kpk-41, 3× kro-rozwod) **BM25 nie dopasowuje w ogóle**
   (`tsquery NIE matchuje chunka`) — niezależnie od tego, jak blisko dany chunk jest w wyszukiwaniu
   gęstym (od pozycji #14 do #2899).
2. **Rozjazd A/B vs C jest niejednolity między chunkami**:
   - `kpk-41` §1: exact #963 → HNSW nieobecny w top-200 (spójne — poza oknem sondy niezależnie od metody).
   - `kpk-41` §2: exact #67 → HNSW #65 (zgodne, brak rozjazdu).
   - `kro-rozwod` §1: exact **#14** → HNSW **nieobecny w top-200** (rozjazd — dokładne wyszukiwanie
     stawia chunk wysoko, przybliżone HNSW go nie znajduje wcale w oknie 200).
   - `kro-rozwod` §2: exact #2899 → HNSW nieobecny (spójne, oba daleko).
   - `kro-rozwod` §3: exact #294 → HNSW nieobecny (poza oknem sondy w obu, brak możliwości
     porównania rozjazdu przy tej pozycji).
3. Żaden z 5 zbadanych chunków nie wszedł do puli `dense@50` ani `sparse@50` — wszystkie kończą
   jako „poza pulą RRF" niezależnie od finalnego `TopK` (bo `CandidatesPerPath=50` obcina PRZED
   fuzją i przed jakimkolwiek etapem, na który `TopK` ma wpływ).
4. `HybridRetriever.cs` (kod, nie pomiar): `CandidatesPerPath` (domyślnie 50, `RetrievalOptions`)
   ogranicza liczbę kandydatów pobieranych z KAŻDEJ ścieżki (dense, sparse) PRZED fuzją RRF
   (linia 42: `var k = query.CandidatesPerPath`). Po fuzji, `Take(query.TopK * 4)` (linia 126)
   przycina połączoną listę. Reranker (linia 167-169) działa WYŁĄCZNIE na tym, co przetrwa oba
   te kroki (`deduped`) — nie ma dostępu do niczego, co nie weszło do `CandidatesPerPath` ani nie
   przetrwało `Take(TopK×4)`.

## Co NIE zostało zmierzone (świadomie, do ewentualnego dalszego badania)

- Czy rozjazd A/B vs C dla `kro-rozwod` §1 (exact #14 → HNSW nieobecny) jest odosobnionym
  przypadkiem, czy systematycznym wzorcem — zbadane na JEDNYM chunku, nie na próbce.
- Czy podniesienie `CandidatesPerPath` (np. 50→100+) faktycznie poprawia `Recall@K` na golden-set
  lub refusal-set — niezmierzone, tylko wywnioskowane z mechaniki kodu.
- Koszt/latencja rerankera przy szerszej puli kandydatów — nieszacowany w tym dokumencie.
- Zasięg problemu braku dopasowań BM25 (czy to zawsze brak stemmingu, czy inne przyczyny per
  przypadek) — zaobserwowano `tsquery NIE matchuje` 5/5 razy, przyczyna nie została zbadana
  osobno dla każdego przypadku (adnotacja narzędzia „patrz Case 4" odsyła do wcześniejszego
  ustalenia w `docs/RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md`, nie do nowego pomiaru tutaj).

## Powiązane

- [RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md](RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md) — Case 4
  (BM25/AND-koniunkcja), wcześniejsze pomiary `TopK×4`.
- [PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md](PROBLEM-WYSZUKIWANIE-PO-SYGNATURZE.md) — analogiczny wzorzec
  („dokładny identyfikator gubiony przez ogólny retrieval semantyczny/leksykalny"), tam już naprawiony
  dedykowanym lane'em.
