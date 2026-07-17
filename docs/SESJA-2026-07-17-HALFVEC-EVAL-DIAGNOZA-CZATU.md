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

- **Act-only retrieval lane** (sekcja 5) — główny następny krok jakościowy.
- Migracja EF odzwierciedlająca realny indeks halfvec (sekcja 1).
- Rozjazd lokalny `main` vs `origin/main` (nasz `16adaf2` nie na origin; origin `3fa20b5` scalony do
  gałęzi) — do uporządkowania.
- Wcześniej flagowane: paralelizacja pipeline'u ingestii; twardsza obsługa `HttpClient.Timeout`
  w ingescie (retry z sekcji 3 częściowo to adresuje, ale nie zweryfikowano oryginalnego scenariusza).
- Diagnostyczny hook `PRAWORAG_DUMP_PROMPT` — opt-in, zostaje jako narzędzie do debugowania jakości.
