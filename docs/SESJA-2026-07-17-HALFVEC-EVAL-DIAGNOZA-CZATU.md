# Sesja 2026-07-17 — halfvec: odbudowa indeksu, retriever, ewaluacja, diagnoza jakości czatu

Dziennik wniosków z jednej sesji, od odbudowy indeksu wektorowego po produkcyjnym bulk-insercie
pełnego korpusu aż po diagnozę, dlaczego czat potrafi dać śmieciową odpowiedź. Wszystko na gałęzi
`feat/halfvec-retriever`.

Punkt wyjścia: pełny korpus (7 427 688 chunków / 523 093 dokumentów) był już w bazie na maszynie z
RTX 3060 (`192.168.100.11`), ale indeksy HNSW i GIN były **celowo usunięte** przed bulk-insertem i
trzeba je było odbudować, a potem sprawdzić jakość.

---

## 1. Odbudowa indeksów

### GIN (full-text, `IX_chunks_SearchVector`)
Zbudował się przy pierwszej próbie w **15 min 20 s**. Bez komplikacji.

### HNSW (wektor, `IX_chunks_Embedding`) — trzy podejścia
Fp32 na tej maszynie się nie mieści w pamięci. Sekwencja:

1. **fp32 + parallel workers** → padło na limicie `/dev/shm` w Dockerze (parallel index-build koordynuje
   się przez DSM w `/dev/shm`, domyślnie za mały). Naprawa: `max_parallel_maintenance_workers = 0`.
2. **fp32 single-threaded** → policzone empirycznie: pełny graf potrzebowałby ~33 GB
   (≈222 868 tuple/GB, ~4,5 KB/tuple). Za dużo — realny sufit środowiska WSL2/Docker to **~15 GB**,
   nie zakładane 32 GB (potwierdzone `docker stats`: MEM LIMIT 15 GB; TEI zjada ~130 MB).
3. **halfvec (fp16)** — indeks WYRAŻENIOWY na `Embedding::halfvec(1024)`. Nie dotyka danych, nie wymaga
   re-embeddingu, w pełni odwracalny (drop + fp32 na mocniejszej maszynie później). Pierwsza próba padła
   na OOM przy 87,2% (`maintenance_work_mem=20GB` > 15 GB środowiska). **Trzecia, udana**: `12GB`,
   `max_parallel_maintenance_workers=0`, ~**12,5 h**. Przetrwała nawet zerwanie lokalnego klienta psql
   w ostatnim 1% (backend serwerowy dokończył — CREATE INDEX jest nieinteraktywny).

### Stan końcowy (zweryfikowany)
- `chunks` = 7 427 688 (bez utraty danych), backup zrobiony i porównany wcześniej.
- `IX_chunks_Embedding`: `indisvalid=t`, `indisready=t`, rozmiar **18 GB**.
- Komplet 6 indeksów na `chunks`.

**Rozjazd modelu EF vs rzeczywistość:** [PrawoRagDbContext.cs](../src/PrawoRAG.Storage/PrawoRagDbContext.cs)
wciąż deklaruje `IX_chunks_Embedding` jako `vector_cosine_ops`, a realny indeks to wyrażenie na halfvec.
Nieszkodliwe dopóki nikt nie odpali migracji od zera — kandydat na migrację odzwierciedlającą stan faktyczny.

---

## 2. Poprawka retrievera (commit `d987916`)

LINQ `CosineDistance` porównywał fp32↔fp32 i **nie trafiał** w nowy wyrażeniowy indeks halfvec → pełny
sequential scan po 7,4 mln wierszy przy każdym zapytaniu. Tor gęsty w
[HybridRetriever.cs](../src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs) przepisany na surowe,
sparametryzowane SQL z rzutem `::halfvec(1024)` po obu stronach `<=>`, z filtrami (CourtType, DateFrom/To,
OnlyInForce, MinChunkTokens) przeniesionymi 1:1. Testy jednostkowe (`HybridRetrieverTests`, 5/5) zielone.

Scalono też z `origin/main` gotowy, wcześniej zbudowany równolegle runner egzaminacyjny `--exam`
(commit `3fa20b5`, merge `81809d1`).

---

## 3. Ewaluacja egzaminacyjna (commit `fe4a5b8`, szczegóły w [EWALUACJA-EGZAMIN-HALFVEC-2026-07-17.md](EWALUACJA-EGZAMIN-HALFVEC-2026-07-17.md))

446 pytań ABC × 3 tryby (solo/rag/oracle), lokalny Bielik + korpus na 3060, **5 h 41 min**, zero wyjątków.

| Tryb | Trafność |
|---|---|
| solo (goły Bielik) | 71,1% (317/446) |
| **rag (system)** | **81,6%** (364/446) |
| oracle (sufit) | 77,8% surowo → **96,1%** na pytaniach z pokrytą podstawą |

Próg ludzi 66,7%. `rag − solo` = +10,5 pp. **Artykuł z wykazu w kontekście: 326/381 (85,6%), 100% przez
tor `dense`** — potwierdza, że poprawka halfvec działa na produkcyjnym korpusie. Halfvec (fp16) **nie
pogorszył** jakości względem oczekiwań fp32.

**Ważne zastrzeżenie, które okazało się kluczowe później:** eval mierzy wybór JEDNEJ litery A/B/C —
o jakości GENERACJI SWOBODNEJ nie mówi nic. 81,6% nie było dowodem, że czat daje dobre odpowiedzi.

Dodano też retry na przejściowych błędach transportu w
[TeiEmbeddingProvider.cs](../src/PrawoRAG.Embeddings/TeiEmbeddingProvider.cs) (`WithRetryAsync`, do 6 prób,
~42 s okna) — zabezpiecza wielogodzinne biegi przed jednym zerwanym połączeniem.

---

## 4. Diagnoza jakości czatu — najważniejszy wniosek dnia

**Objaw:** na pytanie *„Sąsiad twierdzi, że drzewo z mojej działki spadło na jego ogrodzenie i żąda
odszkodowania. Drzewo było zdrowe, przewróciła je wichura. Czy odpowiadam za taką szkodę?"* czat zamiast
odpowiedzieć wyprodukował **streszczenie jednego orzeczenia** w formacie „Zadanie:/Odpowiedź:" (opis
łódzkiej sprawy o topolę i samochód).

### Droga diagnostyczna (i błędy po drodze)
Ścieżka była pouczająca — dwie hipotezy odrzucone twardymi danymi, zanim znaleźliśmy przyczynę:

1. **Hipoteza: przepełnienie kontekstu (num_ctx=4096).** Bielik w Ollamie serwowany z oknem 4096 tokenów,
   a prompt czatu ma 8 źródeł. **Obalone logami Ollamy**: realne zapytanie miało 2050 tokenów,
   `truncated = 0`. Nic nie zostało obcięte. Podnoszenie num_ctx nic by nie dało.
2. **Hipoteza: narracyjne case-law wywołuje streszczanie.** Częściowo trafna, ale pod kontrolowanym testem
   Bielik zwykle ODPOWIADAŁ poprawnie (nawet na 5 różnych orzeczeniach, nawet na jednym pociętym na chunki).
   Reprodukcja udawała się tylko na skrajnie zdegenerowanym wejściu. **Przeceniona pewność** — słusznie
   zakwestionowana przez użytkownika.
3. **Dowód rozstrzygający: zrzut realnego promptu** (dodany opt-in hook `PRAWORAG_DUMP_PROMPT` w
   [OpenAiCompatibleLlmProvider.cs](../src/PrawoRAG.Llm/OpenAiCompatibleLlmProvider.cs)). Pokazał, co
   NAPRAWDĘ dostał Bielik: **8 na 8 źródeł to orzeczenia SAOS, ZERO statutu.** Część to surowe ustalenia
   faktyczne bez tez, część sprzeczna (jedne: siła wyższa/brak winy; inne: zły stan drzewa → jest
   odpowiedzialność). Model dostał stos narracji bez normy prawnej i je streścił.

### Potwierdzenie root-cause (zmierzone, `/api/search` TopK=50)
- **Pytanie naturalne:** top-14 to same orzeczenia; pierwszy akt dopiero #15 i to ZŁY (art. 149 KC
  o gałęziach nad granicą, nie art. 415 o odpowiedzialności); **art. 415 KC nieobecny w top-46**.
- **Zapytanie normatywne** („odpowiedzialność za szkodę z winy… siła wyższa"): **0 aktów w top-49**.
- **Test izolujący na Bieliku (localhost):** gdy w źródłach jest art. 415 + 361 KC, model odpowiada
  **bezbłędnie** (konkluzja + cytaty). Czyli to NIE słabość modelu — to skład źródeł.

### Wniosek
**Governing statute (np. art. 415 KC) jest nieretrievalny dla pytań opisowych.** Korpus 500k+ orzeczeń
SAOS grzebie garstkę kodeksów: krótki abstrakcyjny przepis przegrywa podobieństwo dense+BM25 z narracjami
spraw, które mają te same słowa plus mnóstwo konkretów. Retrieval strukturalny (QU) nie ratuje — działa
tylko na jawny cytat „art. X" w pytaniu. Bielik, dostając same narracje bez normy, streszcza dokument.

Dlaczego eval tego nie złapał: pytania egzaminacyjne mają jawne podstawy prawne i wymagają jednej litery;
generacja swobodna z pytania NL to inny, znacznie wrażliwszy przypadek.

---

## 5. Naprawa do zrobienia — osobny tor retrievalu tylko-akty

Prosta „podłoga statutowa" (promować akty z puli kandydatów) **nie zadziała** — właściwego przepisu
w puli nie ma (jest przypadkowy art. 149 albo nic). Właściwe rozwiązanie: **dedykowana ścieżka
`WHERE DocType='act'`**, w której art. 415 konkuruje wyłącznie z innymi przepisami (nie z 500k orzeczeń),
i której wynik dokładamy do ugruntowania obok orzeczeń.

Ryzyko do przetestowania: czy w act-only lane art. 415 wygra z art. 149 dla tego pytania. Wymaga iteracji
na maszynie 3060 (środowisko agenta nie sięga sieci lokalnej — biegi odpala operator).

### 5a. Sonda diagnostyczna `--probe-akty` (2026-07-18) — pomiar PRZED implementacją

Analiza na chłodno podważa prosty act-only lane: art. 415 KC („kto z winy swej…") nie dzieli z pytaniem
o drzewo ANI JEDNEGO słowa, a art. 149 KC dosłownie zawiera „gałęzie/drzewa/grunt sąsiedni" — w puli samych
aktów pułapka leksykalna prawdopodobnie wygrywa. Alternatywa rozwiązująca problem u źródła: **most cytowań**
— trafione orzeczenia (retrieval orzeczeń działa dobrze!) same cytują w treści przepisy, na których się
opierają („na podstawie art. 415 k.c."); ekstrakcja tych cytowań + dociągnięcie artykułów istniejącym
mechanizmem strukturalnym daje normę bez ML i bez dodatkowego wywołania LLM.

Zamiast wybierać na wiarę — sonda `PrawoRAG.Eval --probe-akty` mierzy na żywej bazie (tylko odczyt):
**A** sanity korpusu (czy art. 415/361/435/149 KC w ogóle są i mają embedding), **B** pełny dense top-50
(niezależna weryfikacja dowodów z tej sesji), **C** act-only dense top-20 (kto wygrywa: 415 czy 149),
**D** dry-run mostu cytowań (co cytują chunki top-orzeczeń; głosowanie per NIEZALEŻNY dokument, żeby jedno
orzeczenie z litanią przepisów procesowych nie zdominowało wyniku). Parser cytowań stylu orzeczniczego
(`JudgmentCitationParser` — skrót aktu musi PRZYLEGAĆ do numeru; precyzja ponad recall) pokryty testami.

Uruchomienie (M4, baza+TEI na 3060):
```bash
ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag" \
Embeddings__BaseUrl=http://192.168.100.11:8080 \
dotnet run --project src/PrawoRAG.Eval -- --probe-akty
```
Bez argumentów: oba pytania z tej diagnozy (naturalne + normatywne). Własne pytanie: dopisać po fladze.
Decyzja projektowa (act-only lane vs most cytowań vs hybryda) zapada PO odczycie wyniku sondy.

### 5b. Wynik sondy (2026-07-18) — DECYZJA: most cytowań, act-only lane odrzucony

Sonda odpalona na żywym korpusie (3060). Rozstrzygnęła jednoznacznie i **obaliła act-only lane zanim
powstał kod**.

**A — sanity:** art. 415, 361, 435 (właściwe) i 149 (pułapka) — wszystkie SĄ w korpusie i mają embedding.
Czyli to nie luka korpusu, tylko ranking.

**Pytanie o drzewo (naturalne, realny typ zapytania):**

- **C — act-only dense top-20 (test act-only lane):**
  ```
  #1 art. 149  ◄ pułapka (sim 0,7949)
  #2 art. 148
  #3 art. 154
  → art. 415: NIEOBECNY w top-20 aktów. Pułapka 149 na #1.
  ```
  **Act-only lane NIE działa** — nawet w puli samych aktów właściwa norma nie wychodzi, wygrywa
  leksykalna pułapka art. 149 („drzew gałęzi", „grunt sąsiedni"). Intuicyjny fix odrzucony przez pomiar.

- **D — most cytowań (dry-run, głosowanie per niezależny dokument):**
  ```
  Chunków orzeczeń: 30; cytowań z aktem: 10
  KC art. 415 — 3 dok. / 3 wyst. — w korpusie: JEST  ◄◄◄ norma właściwa, wygrywa głosowanie
  (dalej: art. 144, 354, 822, 222, 433, 361 — po 1 dok.)
  ```
  **Most cytowań DZIAŁA** — trafione orzeczenia same cytują art. 415 k.c. (3 niezależne dokumenty,
  najczęstszy), a retrieval orzeczeń działa dobrze. Wyłuskanie cytatów + dociągnięcie tekstu art. 415
  (jest w korpusie, sekcja A) daje normę deterministycznie, bez ML i bez dodatkowego LLM.

**Pytanie normatywne (abstrakcyjne):** B — 0 aktów w top-50 (potwierdza diagnozę). D — 0 cytowań w top-30
orzeczeń. Czyli most cytowań zależy od tego, że trafione orzeczenia cytują przepisy w parsowalny sposób —
działa dla **konkretnych pytań faktowych** (realny ruch), słabiej dla abstrakcyjnych fraz. Akceptowalne.

**DECYZJA:** implementować **most cytowań** w `HybridRetriever`: po pobraniu orzeczeń przepuścić ich tekst
przez `JudgmentCitationParser`, policzyć najczęściej cytowane artykuły (głosowanie per dokument), dociągnąć
ich tekst istniejącym mechanizmem strukturalnym i dołożyć do źródeł przed budową promptu. Act-only lane i
prosta „podłoga statutowa" — odrzucone (brak pokrycia w danych). Do przetestowania na 3060 po implementacji.

---

## 6. Zmierzone liczby (pod przyszłe decyzje / sizing)

- Budowa HNSW halfvec: ~12,5 h @ `maintenance_work_mem=12GB`, single-thread, na 3060/WSL2 (~15 GB RAM).
  Indeks 18 GB. GIN: 15 min 20 s.
- Bielik 11B (Q8_0) na M4 przez Ollama: **~6 tok/s**, 46-70 s na krótką odpowiedź, GPU ~99% util
  (CPU pozornie idle — cały ciężar na Metal/GPU). Zero kosztu API.
- Eval: 446×3 = 1338 wywołań w 5 h 41 min (~15 s/wywołanie); ~1,32 mln tokenów wejścia, ~4,5 tys. wyjścia.
- Sieć M4↔3060 (Wi-Fi): TCP connect ~6-8 ms, /embed ~175 ms, SQL ~127 ms — nie jest wąskim gardłem.

---

## 7. Otwarte wątki

- **Most cytowań w `HybridRetriever`** (sekcja 5b) — NASTĘPNY KROK. Sonda rozstrzygnęła: act-only lane
  odrzucony (art. 415 nieobecny nawet w act-only top-20, pułapka 149 wygrywa), most cytowań ma pokrycie
  (art. 415 cytowany w 3 orzeczeniach top-30). Klocki gotowe: `JudgmentCitationParser` (17 testów) +
  retrieval strukturalny do dociągnięcia tekstu artykułu. Do implementacji i testu na 3060.
- Migracja EF odzwierciedlająca realny indeks halfvec (sekcja 1).
- Rozjazd lokalny `main` vs `origin/main` (nasz `16adaf2` nie na origin; origin `3fa20b5` scalony do
  gałęzi) — do uporządkowania.
- Wcześniej flagowane: paralelizacja pipeline'u ingestii; twardsza obsługa `HttpClient.Timeout`
  w ingescie (retry z sekcji 3 częściowo to adresuje, ale nie zweryfikowano oryginalnego scenariusza).
- Diagnostyczny hook `PRAWORAG_DUMP_PROMPT` — opt-in, zostaje jako narzędzie do debugowania jakości.

---

## 8. Załączniki — surowe dowody

### A. Śmieciowa odpowiedź czatu (objaw)
Pytanie: *„Sąsiad twierdzi, że drzewo z mojej działki spadło na jego ogrodzenie i żąda odszkodowania.
Drzewo było zdrowe, przewróciła je wichura. Czy odpowiadam za taką szkodę?"*

Zamiast odpowiedzi model wyprodukował streszczenie JEDNEGO orzeczenia w formacie zadania na czytanie
ze zrozumieniem (fragment):

```
Zadanie:
1. Przedstawienie sprawy sądowej dotyczącej uszkodzenia samochodu przez gałęzie drzewa.
2. Opis zdarzenia i jego skutków.
...
Odpowiedź:
1. Sprawa dotyczyła uszkodzenia samochodu przez gałęzie drzewa, które spadły na pojazd podczas
   silnego wiatru.
2. Zdarzenie miało miejsce w Łodzi, gdzie w dniu zdarzenia padał deszcz i wiał silny wiatr...
3. Właścicielem nieruchomości (...) była Gmina Miasta Ł. (...)
...
Podsumowanie: (...) Sąd Okręgowy w Łodzi uznał, że Gmina Miasta Ł. (...) nie ponosi odpowiedzialności
za szkodę. Powództwo zostało oddalone (...)
```
Model streścił łódzką sprawę o topolę i samochód — nie odpowiedział na pytanie o ogrodzenie i wichurę.

### B. Realny prompt — co dostał model (zrzut `PRAWORAG_DUMP_PROMPT`)
System prompt był poprawny (zakazuje streszczania, każe odpowiadać wprost). Sekcja ŹRÓDŁA zawierała
**8 orzeczeń SAOS, zero statutu**:

| # | Źródło | Uwaga |
|---|---|---|
| 1 | Sąd Okręgowy w Elblągu, I Ca 262/13 | narracja: lipa, altanka, silny wiatr |
| 2 | Sąd Rejonowy w Dzierżoniowie, I C 56/15 | narracja: drzewo na samochód, opis uszkodzeń, polisa |
| 3 | Sąd Okręgowy w Łodzi, III Ca 2106/18 | narracja: brzoza na ogrodzenie i wiatę |
| 4 | Sąd Rejonowy w Gdyni, I1 C 250/21 | **przeciwny wynik** — zły stan drzewa → JEST odpowiedzialność |
| 5 | Sąd Rejonowy dla Łodzi-Śródmieścia, I C 144/18 | **przeciwny** — wypróchniały pień, brak siły wyższej |
| 6 | Sąd Rejonowy w Golubiu-Dobrzyniu, I C 305/15 | narracja: sosna, trąba powietrzna |
| 7 | Sąd Okręgowy w Łodzi, III Ca 1615/20 | **to streścił model** — topola, siła wyższa, brak winy |
| 8 | Sąd Rejonowy w Sokółce, I C 86/15 | narracja: spór o dąb nad granicą |

Materiał: surowe ustalenia faktyczne, część bez tez, wyniki **sprzeczne** — i żadnej normy prawnej.

### C. Rankingi `/api/search` (TopK=50) — statut nieretrievalny
**Pytanie naturalne** (`maxSim=0.8151`):
```
#1-14  [orzeczenia]  sim 0.80-0.815
#15    [AKT] Kodeks cywilny, art. 149   <- ZŁY przepis (gałęzie nad granicą, nie odpowiedzialność)
--> AKTów w top-46: 1 (i to niewłaściwy). art. 415 KC: nieobecny.
```
**Zapytanie normatywne** („odpowiedzialność za szkodę z winy… siła wyższa", `maxSim=0.8745`):
```
#1-49  [same orzeczenia]
--> AKTów w top-49: 0. art. 415 KC: nieobecny nawet przy zapytaniu językiem przepisu.
```

### D. Test izolujący — Bielik ze statutem odpowiada BEZBŁĘDNIE
To samo pytanie, ale w źródłach art. 415 + 361 KC (~1878 tok, rozmiar jak realna awaria), Bielik lokalny:
```
Nie jesteś odpowiedzialny za szkodę spowodowaną przez drzewo, które przewróciła wichura. Zgodnie z
art. 415 Kodeksu cywilnego [1], aby ponosić odpowiedzialność odszkodowawczą, musi wystąpić wina
sprawcy szkody. W tym przypadku (...) nie można mówić o Twojej winie. (...) wichura jako siła wyższa
zwalnia Cię z odpowiedzialności [2].
```
Dowód, że to NIE słabość modelu, tylko skład źródeł.

### E. Badanie „czy kontekst się mieści" — obala hipotezę o num_ctx

Hipoteza wyjściowa: Bielik w Ollamie serwowany z małym oknem, prompt czatu (8 źródeł) przepełnia je,
`--context-shift` kaleczy instrukcje → streszczanie. Sprawdzone czterema niezależnymi obserwacjami — WSZYSTKIE
przeczą przepełnieniu:

**E.1 — Modelfile: okno faktycznie małe, ale model natywnie duży.**
```
TEMPLATE "<|im_start|>system
{{ .System }}<|im_end|>
<|im_start|>user
{{ .Prompt }}<|im_end|>
<|im_start|>assistant
"
PARAMETER num_ctx 4096      <- serwowane okno
PARAMETER num_predict 512
```
Bielik natywnie obsługuje `context_length = 32768` (z `/api/tags`) — okno 4096 to konfiguracja Ollamy, nie
limit modelu. Gdyby przepełnienie było przyczyną, byłaby to dźwignia do podniesienia.

**E.2 — Aplikacja i tak NIE wysyła num_ctx.** [OpenAiCompatibleLlmProvider.cs](../src/PrawoRAG.Llm/OpenAiCompatibleLlmProvider.cs)
buduje `ApiRequest` bez pola `options`/`num_ctx` — jest zdana wyłącznie na `PARAMETER num_ctx` z Modelfile.
Nie ma per-request override, więc każda dyskusja o num_ctx sprowadza się do Modelfile/env, nie do kodu.

**E.3 — Realny prompt się MIEŚCIŁ, powtarzalnie.** Log Ollamy dla zapytania o drzewo (kilka powtórzeń,
task 0 / 1376 / 1780 / 1978 — użytkownik odpalał je wielokrotnie):
```
slot ... | new prompt, n_ctx_slot = 4096, n_keep = 4, task.n_tokens = 2050
slot ... | stop processing: n_tokens = 2449, truncated = 0
```
2050 tok < 4096, `truncated = 0` za każdym razem. Nic nie zostało obcięte.

**E.4 — Prompt „mieszczący się" i tak daje dobrą odpowiedź.** Test A (2 krótkie art. KC, ~500 tok, głęboko
w oknie) → poprawna, scytowana odpowiedź. Test izolujący (~1878 tok, ten sam rozmiar co realna awaria, sam
statut) → poprawna odpowiedź (załącznik D). Czyli rozmiar mieszczący się w oknie NIE jest przyczyną — o wyniku
decyduje TREŚĆ źródeł (statut vs stos narracji orzeczeń), nie ich długość.

**Wniosek E:** podnoszenie num_ctx nic by nie dało — problem nie jest po stronie kontekstu, tylko składu
retrievalu (sekcja 4).
