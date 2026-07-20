# Plan: Jakość retrievalu na pełnym korpusie (JAK) — z raportu odmów 2026-07-18/19

Wejście: `RAPORT-DIAGNOSTYCZNY-ODMOWY-2026-07-18.md` (6 case'ów, 5 mechanizmów) + feedback
właściciela (2026-07-19). Zasada nadrzędna: **pomiar przed naprawą** (lekcja act-lane — intuicyjny
fix obalony sondą) i weryfikacja wyłącznie na pełnym korpusie.

Stan zastany (naprawione wcześniej, w HEAD): atraktor augmentera (`fc887d6` — potwierdzone
działaniem: pytanie o spadek dostaje odpowiedź), REGULATION (`09eac56`), batch rerankera
(`c30f57d`), most cytowań, reguła 3 zmiękczona (`1f0b8d6`).

Priorytety wg feedbacku: JAK-1/2 (śmieci) TAK; JAK-3 (sonda chunka) MUSIMY; JAK-4 (pochodzenie)
MUSIMY; odporność (JAK-6) tylko jeśli proste/nisko; akronimy przeprojektowane na samoskalujące
(JAK-5); Case 2 (zapytanie kontekstowe przy załączniku) odłożone za rdzeń.

---

## JAK-0: pomiar skali śmieci anonimizacyjnych (SQL, bez kodu)
Analogicznie do zmierzonego `(pominięty)` (1056 chunków): policzyć chunki orzeczeń zdominowane
wzorcami anonimizacji („(...)", „(…)", powtórzone frazy dat/kryptonimów). Szkic miary w SQL:
udział wystąpień `(...)`/`(…)` względem długości tekstu + niska liczba RÓŻNYCH słów znaczących.
**Wyjście:** liczba + losowa próbka 50 do przejrzenia okiem (fałszywe pozytywy?). Bez akceptacji
próbki nie ma JAK-1.

## JAK-1: neutralizacja śmieciowych chunków w bazie (batch, odwracalny)
- Zakres: chunki z JAK-0 + `(pominięty)`/`(pominięte)` placeholdery aktów (1056).
- Mechanizm: **`UPDATE ... SET "Embedding" = NULL`** dla zakwalifikowanych → znikają z toru
  gęstego od ręki (`WHERE Embedding IS NOT NULL` już jest), bez migracji i bez reprocessingu.
  Odwracalne (re-embedding: pipeline sam uzupełni po `EmbeddedWith`/`Embedding IS NULL` — UWAGA:
  sprawdzić, czy fast-skip nie uzna dokumentu za gotowy; jeśli tak, zapisać listę Id do pliku
  przed UPDATE jako ścieżkę powrotu).
- Tor rzadki (BM25): te chunki raczej nie wygrywają leksykalnie (śmieci wygrywały semantycznie);
  w pierwszej iteracji NIE filtrować BM25 — mniej ruchomych części, zmierzyć czy wystarczy.
- Skrypt jako tryb Eval `--sanitize-chunks` (dry-run domyślnie: raport co by wyciął; `--apply`
  wykonuje) — powtarzalny po każdym przyszłym imporcie.
- Weryfikacja: powtórka Case 4/5 przez `/api/search` — placeholdery i frazy anonimizacyjne
  znikają ze źródeł; `--refusals` (retrieval-only) przed/po.

## JAK-2: filtr degeneracji w chunkerze (trwałość, przy okazji następnego processingu)
Ten sam wskaźnik co w JAK-0 wbudowany w `TokenAwareChunker` (rozszerzenie `MinSubstantiveWords`
o miarę powtarzalności/udziału tokenów anonimizacji), żeby reprocessing nie odtwarzał śmieci.
Czyste testy na dosłownych wzorcach z Case 5. Niski priorytet czasowy (odpala się dopiero przy
reprocessingu), ale mały — można domknąć razem z JAK-1.

## JAK-3: sonda `--probe-chunk` (Case 5 — NAJPOWAŻNIEJSZE; pomiar PRZED jakąkolwiek naprawą)
Dla zadanego pytania + wskazania oczekiwanego chunka (ELI/artykuł albo fragment tekstu) raportuje:
1. pozycję chunka w DOKŁADNYM skanie cosine (seq scan po pełnym korpusie — wolne, ale to sonda);
2. pozycję w wyszukiwaniu indeksowym HNSW (halfvec, ef_search=400 — jak produkcja);
3. pozycję w torze BM25 (osobno: pełne pytanie i frazy kluczowe);
4. czy przeżywa fuzję RRF + dedup (symulacja na kandydatach).
Rozjazd (1)↔(2) = strata recall indeksu (halfvec/ef_search) → naprawa po stronie indeksu.
Nisko w (1) = chunk rozmyty (402 tok × 6 podtematów) → kierunek: re-chunking per ustęp.
OK w (1)(2), brak w finale = fuzja/dedup. Zły wynik (3) przy prawie-dosłownym cytacie = osobny
problem konfiguracji tsvector — też do złapania tą sondą.
Przypadek referencyjny: art. 4 ustawy o informowaniu o cenach vs pytanie o „najniższą cenę
z 30 dni". Decyzja o naprawie DOPIERO po odczycie.

## JAK-4: pochodzenie źródła (lane provenance) — koniec zgadywania „skąd ten chunk"
- `RetrievedChunk.Lane` (`dense|lexical|structural|bridge|augmenter`) nadawane w retrieverze
  i augmenterze; przy fuzji chunk obecny w wielu torach → lista/flagi.
- Persystencja w `RetrievedSources` (jsonb — bez migracji) + widoczność w `--refusals`,
  `--probe-*` i (za flagą Diagnostics) w UI przy kartach źródeł.
- Od razu zamyka otwarte pytanie z Case 1 (skąd KPA art. 5/268a — podejrzenie: most cytowań).
- Testy: jednostkowe per tor (fake'i), zgodność serializacji ze starymi rekordami (pole opcjonalne).

## JAK-5: akronimy — samoskalujące, bez kuratorowanej listy (feedback: lista przegra z życiem)
Dwa mechanizmy, oba deterministyczne, zero LLM:
- **5a. Słownik definicji Z KORPUSU (offline):** ekstraktor wzorców definicyjnych z tekstów aktów
  („…, zwany dalej «KSeF»", „Pełna Nazwa (SKRÓT)") → tabela/plik skrót→pełna nazwa. Rośnie sam
  z delta-syncem. QU: wykryty skrót w pytaniu → dopisanie pełnej nazwy do tekstu przed embeddingiem
  (analogia: prefiks „zapytanie:").
- **5b. Mini-tor leksykalny dla rzadkich tokenów CAPS (fallback ogólny):** pytanie zawiera
  wielkoliterowy token spoza stopwordów → dodatkowe dokładne wyszukiwanie chunków zawierających
  ten token (jak tor strukturalny dla „art. X"), z limitem slotów. Ratuje KAŻDY skrót, którego
  korpus gdziekolwiek używa (Case 4: 21 chunków z „KSeF" istniało — AND w websearch_to_tsquery
  je gubił). Known-limitation (jawnie): skrót żywy w żargonie, nieużywany w ustawach (CUW) —
  nierozwiązywalny bez LLM, zostaje na warstwę rozumienia zapytań w przyszłości.
- Kolejność wewnętrzna: 5b najpierw (mniejszy, czysto testowalny, natychmiastowy efekt na KSeF),
  5a po nim.

## JAK-6: odporność + telemetria (niski priorytet — „jeśli proste")
- Reranker fail-open: wyjątek/timeout → ranking RRF + sygnał cosine + log (proste — jeden
  try/catch w retrieverze; eliminuje klasę pustych odpowiedzi).
- Kolumna telemetryczna odmowy progu/treściowej w messages — wymaga migracji; ODŁOŻONE
  (eval `--refusals` klasyfikuje to samo z treści — wystarcza na teraz).
- Persystencja fragmentów [D] za flagą Diagnostics — ODŁOŻONE do następnego case'a z załącznikiem.

## Poza planem (jawnie)
- Case 2 (kontekstowe zapytanie korpusowe przy załączniku) — po ustabilizowaniu rdzenia; wzorzec
  gotowy (podwójny retrieval z marginesem jak follow-upy).
- CUS/CUW (pułapka nazewnicza czysto embeddingowa) — obserwować po JAK-1 (odchwaszczenie może
  wystarczyć rerankerowi); osobna naprawa dopiero z danymi.
- Re-chunking aktów per ustęp — TYLKO jeśli JAK-3 wskaże rozmycie chunka jako przyczynę.

## Kolejność wykonania
JAK-0 (SQL, operator+agent) → JAK-3 (sonda; agent pisze, operator odpala) → JAK-1 (po akceptacji
próbki z JAK-0) → JAK-4 → JAK-5b → JAK-5a → JAK-2 → JAK-6a (fail-open). Po JAK-1 i po JAK-5b:
`--refusals` (retrieval-only wystarczy między krokami; pełny z LLM na koniec fazy).
Commit per task; weryfikacja każdego kroku na pełnym korpusie (M4/3060).

## Status (2026-07-20)
- JAK-0/1 **wykonane** (5449 chunków wyzerowanych); JAK-3 **wykonane** (Case 5 rozstrzygnięty:
  kolizje leksykalne → domena rerankera; poszerzanie puli odrzucone). Reranker naprawiony
  (localhost:8081 na M4, nie 3060) → **odmowy 38%→17% — W CELU fazy**.
- JAK-5b **zaimplementowane**: `AcronymDetector` (≥2 wielkie litery, 2–8 znaków; wykluczenia:
  rzymskie, caps-lock; bez kuratorowanej listy) + tor jednotokenowy w fuzji RRF `HybridRetriever`.
  Weryfikacja na M4: pytanie Case 4 o KSeF (chunki z „KSeF" powinny wejść do kandydatów, reranker
  je ustawi) + `--refusals` + test A1 w `dotnet test` (LiveDb).
- Sygnały bramki **ROZDZIELONE** (decyzja: kalibrujemy przed pilotażem): `MaxSimilarity` znów
  ZAWSZE cosine (bramka + diagnostyka, stabilna skala), top-score rerankera osobno w
  `RetrievalResult.RerankTopScore` (ranking źródeł). `--refusals` pokazuje obie kolumny
  (sim/rr) + tabelę kalibracyjną rozkładów per wynik.

### Kalibracja progu bramki (procedura przed deployem)
1. `--refusals` (z generacją) → sekcja „KALIBRACJA" w podsumowaniu: min/śr/max obu sygnałów
   dla OK vs odmowa/błąd.
2. Wybór: sygnał, którego zakresy się nie nakładają (lub najmniej), na bramkę; próg między
   max(odmowa) a min(OK) — Z MARGINESEM (sygnały zaszumione; koszt fałszywej odmowy przy
   pytaniu w korpusie > koszt przepuszczenia — bramka ma łapać oczywiste pudła, resztę robi
   odmowa treściowa LLM).
3. Ustawić `Retrieval:AbstentionThreshold` w appsettings API (dziś 0.0 = wyłączona) i strażnik:
   golden set (kategoria abstencji) + ponowny `--refusals` — zero regresji „OK→odmowa-progu"
   na pytaniach pokrytych korpusem.
- Otwarte z planu: JAK-4 (lane provenance), JAK-5a (słownik definicji z korpusu), JAK-2 (filtr
  w chunkerze), JAK-6a (fail-open rerankera — po znalezisku o sygnale wart pakietowania razem).

## Runbook operatora (wszystko z M4; baza+TEI na 3060 — wspólny prefiks)

```bash
export ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag"
export Embeddings__BaseUrl=http://192.168.100.11:8080
```

**JAK-0 (pomiar + próbka, nic nie zmienia):**
```bash
dotnet run --project src/PrawoRAG.Eval -- --sanitize-chunks
```
Wypisze liczby per kategoria + próbkę 50 do oceny okiem + plik logs/sanitize-*.jsonl (pełna lista).

**JAK-1 (po akceptacji próbki — zeruje embeddingi zakwalifikowanych):**
```bash
dotnet run --project src/PrawoRAG.Eval -- --sanitize-chunks --apply
```

**JAK-3 (sonda Case 5 — przypadek referencyjny art. 4 ustawy o cenach):**
```bash
Eval__ProbeTextLike="najniższej cenie tego towaru lub tej usługi, która obowiązywała w okresie 30 dni" \
dotnet run --project src/PrawoRAG.Eval -- --probe-chunk "Jak prawidłowo oznaczyć najniższą cenę z ostatnich 30 dni? Kto jest zobowiązany do oznaczania?"
```
(Alternatywnie wskazanie po akcie: `Eval__ProbeEli=DU/... Eval__ProbeArticle=4`.) Sekcje A-E z
gotową interpretacją na końcu wydruku. Skany dokładne = minuty (seq-scan po 7,4M) — to sonda.

**Pomiar między krokami (szybki, bez LLM):**
```bash
Eval__RefusalsGenerate=false dotnet run --project src/PrawoRAG.Eval -- --refusals
```

## Kryterium wyjścia z fazy
`--refusals` z generacją na pełnym zestawie realnych pytań: odsetek odmów ≤ 25% (cel planu
pilotażu: 10-25%), zero pustych odpowiedzi, cytaty czyste w ≥ 90% odpowiedzi.
