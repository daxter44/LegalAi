# Plan wdrożenia produkcyjnego — kolejność, nie daty

Data: 2026-09-02. Branch: `feat/halfvec-retriever`.

Uzupełnia `PLAN-SIZING-DEPLOY-2026-08-24.md` (sizing, obciążenie, latencja) o warstwę **dostawców
i kolejności działań**. Sizing z tamtego dokumentu obowiązuje, z jedną korektą (§1.2 niżej).

Etykiety jak w dokumencie sizingowym: **[zmierzone]** — realny pomiar; **[cena rynkowa,
zweryfikowana 2026-09-02]** — sprawdzone dziś, z linkiem; **[założenie — do potwierdzenia]** —
zależne od informacji, których nie mam.

---

## 1. Ustalenia, które kształtują kolejność

### 1.1 Domena jest korzeniem zależności

Nie jest „jedną z pozycji na liście" — blokuje trzy rzeczy naraz: rekordy DNS dla Resend
(SPF/DKIM/DMARC + czas na weryfikację), publiczny URL dla webhooków Stripe i TLS, oraz **stronę
i wideo wymagane we wniosku do Microsoftu** (§3.2). Kupno kosztuje kilkadziesiąt złotych rocznie
i nie niesie ryzyka. Wszystko inne czeka na nią bez powodu.

### 1.2 Kredyty wygasają 12 miesięcy od aktywacji Azure, nie 180 dni

**[cena rynkowa, zweryfikowana 2026-09-02]** Dokumentacja Microsoftu dla ofert $1 000 / $5 000 /
$25 000 / $50 000 / $150 000: *„Azure credits will expire one year after you activate Azure."*

To koryguje założenie robocze („180 dni") i **usuwa presję na przepalanie kredytów**. Poprzednia
wersja kalkulacji zakładała, że trzeba wydawać ~$847/mies., żeby wykorzystać całość. Przy 12
miesiącach:

| Wariant | miesięcznie (Azure) | $5 000 starczy na |
|---|---|---|
| **E8ds_v5 (baza+app+Umami) + Premium SSD v2, GPU u CloudFerro** | **$447** | **~11 miesięcy** ✅ |
| + GPU `NC4as_T4_v3` na Azure, tylko 8–17 | $550 | ~9 miesięcy |
| + GPU `NC4as_T4_v3` na Azure, 24/7 | $831 | ~6 miesięcy |

**Wariant rekomendowany to pierwszy wiersz** — GPU stoi u CloudFerro i jest pokryte ich własnym
kredytem €250 (§1.5), więc Azure płaci tylko za bazę i aplikację. $447/mies. wyczerpuje $5 000
w ~11 miesięcy, mieszcząc się w rocznym oknie ważności: **pełna wartość obu kredytów wykorzystana,
bez nadmiarowej maszyny.**

**Nie ma powodu, żeby powiększać serwer „bo kredyty się marnują".**

Ta sama korekta zamyka temat ARM (E8ps_v5, ~$294/mies.). Jego jedyną przewagą jest cena, a cena
przestała być ograniczeniem; kosztuje natomiast przenośność migracji (§1.4) i nie ma odpowiednika
64 GB w ofercie Hetznera (maks. CAX41 = 32 GB) **[zweryfikowane 2026-09-02]**.

⚠️ **Do potwierdzenia przy wniosku:** czy $5 000 przychodzi jednorazowo, czy w transzach z własnymi
zegarami. Nie znalazłem jednoznacznej odpowiedzi.

### 1.3 Akceptacja wniosku ≠ start zegara

Zegar 12 miesięcy startuje przy **aktywacji Azure**, nie przy akceptacji wniosku. To jest dźwignia,
którą warto wykorzystać świadomie: **złóż wniosek wcześnie** (rozpatrzenie ~3 dni robocze, jest
darmowe i nie zobowiązuje), a **aktywuj dopiero, gdy jesteś gotowy budować**. Odwrotna kolejność
oddaje tygodnie kredytów za pustą subskrypcję.

**[założenie — do potwierdzenia]** Potwierdź to w portalu przed aktywacją; opieram się na
sformułowaniu „after you activate Azure", nie na własnym przebiegu.

### 1.4 Managed Postgres odpada, ale nie z powodu, który podawałem wcześniej

Korekta względem pierwszej analizy: dzisiejszy schemat **wjeżdża na Azure Flexible Server bez
zmian** — kolumna generowana to `to_tsvector('simple', …)`, a rozszerzenia w użyciu (`vector 0.8.5`,
`pg_trgm`, `plpgsql`) są tam wspierane **[zmierzone: `pg_extension` na `.11`]**. Polski hunspell jest
przygotowany, ale nieużywany (`IX_chunks_SearchVector_pl`: 2,7 GB, **0 skanów**).

Powód wyboru gołej VM jest więc **cenowy i opcyjny**, nie techniczny:

- managed E8ds_v5 $730/mies. + osobny hosting aplikacji ~$70–150 vs **VM $421 z aplikacją i Umami
  na tej samej maszynie**;
- managed trwale zamyka przełączenie na `polish`, którego zysk jest już zmierzony
  (`RUNBOOK-SLOWNIK-POLSKI-FTS.md`: „przedawnienie roszczenia" ↔ „przedawnienia roszczeń" = `f`
  przy `simple`, `t` przy `polish`).

Managed kupuje realną rzecz — automatyczne backupy i PITR. Na VM odpowiednikiem jest
`pg_basebackup` + archiwizacja WAL (§5.7), i to **musi** zostać zrobione, bo `pg_dump` + 12,5 h
rebuildu HNSW nie jest planem odtworzenia.

### 1.5 CloudFerro daje €250 na start — to pokrywa całą ekspozycję CloudFerro na ~5 miesięcy

**[cena rynkowa, zweryfikowana 2026-09-02]** €250 kredytu testowego, **ważne 6 miesięcy od
aktywacji**, przyznawane indywidualnie (CloudFerro zastrzega prawo odmowy), na koncie w ciągu
maks. 1 dnia roboczego. Wniosek przez portal Free Trial na `ecommerce.cloudferro.com`.

> **STATUS: PRZYZNANE 2026-09-03.** Wniosek złożony 2026-09-02 (dwa zamówienia, 1793 i 1794 —
> duplikat formularza), kredyt na koncie następnego dnia, zgodnie z deklarowanym oknem 1 dnia
> roboczego. **Okno 6 miesięcy liczone od tej daty upływa ~2026-03-03.**
> ⚠️ Do potwierdzenia w panelu: (a) czy „aktywacja" to data przyznania, czy pierwszego użycia —
> przyjmuję wariant ostrożniejszy (przyznanie); (b) waluta salda — cała kalkulacja niżej jest
> w EUR, a saldo w USD skróciłoby zasięg o ~8%.

Zestawienie z realnym zużyciem pokazuje, że to **więcej niż wystarczy na wszystko, co macie
u CloudFerro**:

| Pozycja | Koszt/mies. | Uwaga |
|---|---|---|
| GPU `vm.l40s.1`, tylko 8–17 w dni robocze | **€48** | €0,246/h × ~195 h |
| LLM Gemma ~31B (€0,56/1M tok.) przy ~1000 zapytań/mies. | **~€3** | ~5,5k tok./zapytanie **[założenie — do potwierdzenia w §0.2]** |
| **Razem** | **~€51** | **€250 ≈ 4,9 miesiąca** |

Dwa wnioski, oba istotne dla kolejności:

1. **Koszt LLM jest przy skali pilotażu zaokrągleniem, nie pozycją budżetową.** ~€3/mies. przy 1000
   zapytań. Sizing doc słusznie ostrzegał, że przy 1000 userów LLM może przewyższyć koszt serwera —
   ale to jest problem sukcesu, nie startu. **Nie planować wokół niego teraz.**
2. **To neutralizuje największe otwarte ryzyko planu.** Quota GPU na Azure (§4.3) była „bramką dnia
   pierwszego", bo odmowa oznaczała €48/mies. z własnej kieszeni od razu. Teraz odmowa nic nie
   kosztuje przez ~5 miesięcy. **Bramka spada do rangi preferencji.**

Wynikająca z tego zmiana rekomendacji: **startuj na GPU CloudFerro, nie czekaj na quotę Azure.**
Wniosek o quotę złóż i tak (jest darmowy), ale nie blokuj na nim budowy.

Kosztem tego wyboru jest latencja Azure West Europe ↔ Warszawa przy każdym wywołaniu embed/rerank.
**[zmierzone w kodzie 2026-09-02]** Round-tripów jest **więcej, niż się wydaje**:
`TeiReranker.RerankAsync` **pętli po batchach** (`RerankerOptions.MaxBatch = 32` — twardy limit TEI,
przekroczenie zwraca 422), więc przy `CandidatesPerPath=50` i kilku torach pula `deduped` rzędu
100–150 kandydatów daje **4–5 kolejnych wywołań**, plus osobne wywołanie na moście cytowań, plus
jedno na embedding zapytania. Realnie **~6–7 round-tripów na turę czatu**.

Przy RTT 20–30 ms to +120–210 ms na `retrieval.total` o medianie 1,6 s — wciąż do zaakceptowania,
ale to nie jest szum. **Zmierz RTT w §0.6** zamiast przyjmować 20–30 ms na wiarę; jeśli wyjdzie
istotnie więcej, opcją jest podniesienie `MaxBatch` (wymaga TEI z wyższym `max_client_batch_size`)
albo przeniesienie GPU na Azure (§4.3).

⚠️ **Dwa zegary nie startują razem.** €250 od CloudFerro to 6 miesięcy **od aktywacji**, a aktywujesz
w §0.2 — najwcześniejszym kroku planu. Kredyty Azure to 12 miesięcy od aktywacji w §2.2, czyli po
domknięciu Faz 0 i 1. Jeśli te fazy zajmą dwa miesiące, **zegar CloudFerro jest w 1/3 wyczerpany,
zanim Azure w ogóle wystartuje**. Nie liczyć „€250 na 5 miesięcy" tak, jakby oba okna pokrywały się
w czasie.

Konsekwencja budżetowa, jedyne miejsce w planie z realnym wydatkiem w trakcie kredytów: **od ok.
5. miesiąca użycia CloudFerro, gdy €250 się wyczerpie, płacisz ~€51/mies. (GPU 8–17 + LLM) z własnej
kieszeni** — aż do wyczerpania kredytów Azure (§7). Do tego jednorazowo €99 na próbę generalną (§6).

### 1.5a Region: WAW3-2. Sherlock to osobny byt, nie projekt OpenStack

**[zweryfikowane 2026-09-03]** Aktywować i zasilić należy **`openstack_waw3_2`**. Powód nie jest
dowolny: **tylko WAW3-2 ma opublikowane ułamkowe flavory L40S**, na których stoi cała kalkulacja
w §1.5 i §1.5b — `vm.l40s.1` (1/8 L40S, 4 GB VRAM, €0,246/h) oraz warianty spot (od €0,123/h).

| Region | GPU w cenniku |
|---|---|
| **WAW3-2** | ✅ H100 passthrough + **ułamkowe L40S + spot** |
| WAW4-1, LCJ1-1 | tabela GPU istnieje, ale bez opublikowanych ułamków/spotu |
| FRA1-2, FRA1-3 | ❌ brak tabeli GPU |

Sprawdzone też pod kątem RTT z §0.6: Frankfurt byłby bliżej Azure West Europe niż Warszawa, ale
**żaden region FRA nie ma GPU**, więc ten kompromis nie istnieje. WAW3-2 jest jedynym sensownym
wyborem, a RTT trzeba po prostu zmierzyć.

**Sherlock (LLM) NIE jest na liście „Cloud projects".** To osobna usługa z własnym panelem
i własnymi projektami:

1. organizacja w CloudFerro Cloud z numerem VAT/NIP (**zrobione**),
2. projekt w panelu Sherlocka: `https://sherlock.cloudferro.com/panel/`,
3. wygenerowanie klucza (w panelu nazywanego „service key"),
4. endpoint OpenAI-compatible: **`https://api-sherlock.cloudferro.com/openai/v1`** → to jest
   wartość pod `Llm__Local__BaseUrl` w §0.2.

⚠️ **Niewiadoma, której nie rozstrzygnąłem:** czy €250 z free trial pokrywa tokeny Sherlocka, czy
tylko IaaS (VM-y, storage). Nigdzie tego nie opisano. **Nie blokować na tym** — przy skali pilotażu
LLM to ~€3/mies. (§1.5), więc nawet negatywna odpowiedź nie rusza budżetu. Zapytać przy okazji
zgłoszenia duplikatu zamówień 1793/1794.

⚠️ Publikowana lista modeli Sherlocka (Llama 3.1/3.3, Mistral, Bielik, PLLuM, DeepSeek) **nie
wymienia Gemmy** — zgodnie z tym, co wiadomo o nieaktualnej dokumentacji. Dokładny identyfikator
modelu potwierdzić w panelu, bo `Llm__Local__Model` wymaga konkretnego stringa.

### 1.5b Spot GPU — realne przy 1–5 klientach, po naprawie odporności (ZROBIONE)

**[cena rynkowa, zweryfikowana 2026-09-02]** `spot.vm.l40s.1` = **€0,123/h** wobec €0,246/h
on-demand. Przy pracy 8–17: **€24/mies. zamiast €48** — €250 starcza wtedy na ~10 miesięcy zamiast
~5, czyli pokrywa cały okres kredytów Azure i **usuwa okres płacenia z własnej kieszeni** opisany
w §1.5.

Cena stała (nie licytacja), ale maszyna **może zostać ubita w dowolnej chwili bez uprzedzenia**,
a wraz z nią kasowane są trwale zasoby pod nią — łącznie ze storage. Cache modeli HF trzymać na
osobno podpiętym Volume Storage (przeżywa), żeby restart był krótki.

**Blokada, która to uniemożliwiała, i jej usunięcie [zmierzone w kodzie 2026-09-02]:**
`HybridRetriever` wołał cross-encoder w **dwóch** miejscach (`rerank.main` i `rerank.bridge`), oba
**bez `try/catch`**. `TeiReranker` rzuca `HttpRequestException` przy każdym nie-2xx i przy zerwanym
połączeniu, więc ubity spot / restart TEI / 503 / timeout **wywracał całe zapytanie użytkownika**.
Gałąź `else` z zejściem do kolejności RRF łapała wyłącznie reranker wyłączony w DI (`== null`), nie
awarię działającego.

Naprawione TDD (testy `R9`, `R10` w `HybridRetrieverTests`): awaria degraduje **jakość kolejności**
(zejście do RRF, `RerankTopScore=null`), nie wywraca zapytania, i **zostawia `LogWarning`
z przyczyną** — cicha degradacja to ta sama klasa problemu co martwy tor rzadki czy niezliczane
`Abstained`. Anulowanie (`ct`) przepuszczane dalej, wzorem mostu cytowań.

⚠️ **Embedder nadal jest twardą zależnością** — bez niego tor gęsty w ogóle nie rusza, więc pełne
przejście na spot wymaga osobnej decyzji. Rekomendacja: spot dla **rerankera** i dla **burstów
re-embeddingu** (wsadowe, wznawialne — profil idealny), on-demand dla embeddera zapytań.

### 1.6 RAM: 64 GB, z pomiaru

**[zmierzone 2026-09-02, `.11`, pełny korpus 8,38 mln chunków]**

| Test | Wynik |
|---|---|
| To samo zapytanie ANN, drugi raz (ciepło) | 2835 bloków, 100% z RAM → **6,1 ms** |
| To samo zapytanie, pierwszy raz (zimno) | 2835 bloków, 2532 z dysku → **422 ms** |
| **6 różnych zapytań** (sondy z różnych dokumentów) | hit 255–325 / **read 906–2628** → **12–15% trafień** |

Interpretacja: różne zapytania **w większości nie trafiają w cache**. Każde czyta ~2000 świeżych
bloków (~16 MB), a nakłada się z poprzednimi tylko w ~13% — te wspólne ~250–320 bloków to górne
warstwy grafu HNSW. Working set narasta w stronę pełnych 21 GB indeksu, nie saturuje się na małym
gorącym rdzeniu.

Dlatego „na `.11` działa szybko" nie przenosi się na produkcję: podczas developmentu zadaje się
w kółko podobne pytania (ścieżka 6,1 ms); produkcja z różnorodnymi pytaniami to ścieżka 422 ms.

**Nie używać do tego sporu wskaźnika 92,2% hit ratio na `IX_chunks_Embedding`** — to 1,14 mld
odwołań w 2,3 dnia (9,1 TB), czyli build indeksu i przebiegi eval/reprocess o ogromnej lokalności
wewnętrznej, nie obsługa zapytań.

Wniosek: **64 GB**. Przy 32 GB total trzyma się ~20–25 GB przeciw 21 GB HNSW + 3,2 GB GIN, przy
konkurencji aplikacji i Umami — na styk. Spór ma zresztą znikomy wpływ na koszt: na Azure to
kredyty, a Hetzner AX42 **jest** maszyną 64 GB za €99.

**Korpus przyjmujemy jako płaski** — skok 94→106 GB (24.08→02.09) to re-chunking (`unit_pass`,
2307 aktów) i EUR-Lex T1+T2, nie nowe źródła. Prawo UE sprzed 2004 jest poza zakresem, więc floor
RAM jest celem stałym.

---

## 2. Faza 0 — nic nie kosztuje, nic nie uruchamia zegara

Wszystko tutaj da się zrobić **przed** jakimkolwiek kontaktem z Microsoftem i przed wydaniem
pieniędzy. To jest właściwe miejsce na ryzyko.

### 0.1 Kup domenę — ZROBIONE 2026-09-02
Korzeń zależności (§1.1). Odblokowuje jednocześnie: DNS dla Resend (§1.1), publiczny URL webhooków
Stripe i TLS (§1.2), oraz stronę i wideo do wniosku MS (§0.5, §1.3).

### 0.2 Załóż konto CloudFerro, weź €250 i zmierz Gemmę — największe niezmierzone ryzyko produktowe

Najpierw wniosek o €250 (portal Free Trial, `ecommerce.cloudferro.com`, kredyt na koncie w ciągu
maks. 1 dnia roboczego — §1.5). **Ten test jest więc darmowy**, a zegar 6 miesięcy startuje
dokładnie tam, gdzie zaczyna się realne użycie.

`PLAN-SIZING-DEPLOY-2026-08-24.md` §0 i §5 zostawiają puste miejsce na `llm.total` i zużycie
tokenów, bo nie było klucza. **To wciąż jest puste, a cała jakość produktu na tym stoi** —
Gemma ~31B na Sherlocku nigdy nie była testowana na Waszym pipelinie. Kod jest gotowy
(`OpenAiCompatibleLlmProvider`, endpoint OpenAI-compatible), więc to zmiana dwóch zmiennych:

```bash
PRAWORAG_LOG_TIMING=1 Diagnostics__ShowTokenUsage=true \
Llm__Provider=local Llm__Local__BaseUrl=<endpoint Sherlocka> \
Llm__Local__Model=<id Gemmy> Llm__Local__ApiKey=$CLOUDFERRO_API_KEY \
dotnet run --project src/PrawoRAG.Api
```

Przepuść przez to golden-set (`--chat`/`--exam`) i zanotuj: `llm.total`, tokeny wejścia/wyjścia,
jakość odmów. Dwie rzeczy, które mogą tu wyjść i wywrócić harmonogram: Gemma na Sherlocku zachowuje
się inaczej niż w testach, albo €0,56/1M przy realnym zużyciu tokenów daje inny koszt niż szacunek.
**Lepiej dowiedzieć się teraz niż po opłaceniu infrastruktury.**

> **WYNIK 2026-09-03 [zmierzone]** — Gemma na Sherlocku działa na pełnym pipelinie.
> Model potwierdzony w `/models`: **`google/gemma-4-31B-it`** (dokumentacja Sherlocka go nie
> wymieniała). Endpoint: `https://api-sherlock.cloudferro.com/openai/v1`.
>
> | Metryka | PLAN-SIZING (lokalny 26B) | Sherlock / Gemma 31B |
> |---|---|---|
> | `llm.first_token` | — | **659 ms** |
> | `llm.total` | 43–48 s | **6 183 ms** |
> | `chat.total` | 46–50 s | **21 735 ms** |
>
> Brak sekcji rozumowania → model nie emituje myślenia, więc założenie ~1024 tok. wyjścia z §1.5
> się broni, a pułapka pustych odpowiedzi przy małym `MaxTokens` tu nie zachodzi.
>
> ⚠️ **Teza §0 doku sizingowego przestała obowiązywać.** Tam LLM to było 90–96% `chat.total`;
> teraz 28%. **~15,5 s (72%) to nie LLM** — przyspieszenie modelu odsłoniło koszt, który wcześniej
> chował się w jego cieniu. `retrieval.total` miał medianę 1,6 s / p90 3,0 s, więc to poza znanym
> budżetem. Niesprawdzeni podejrzani: rozgrzewka (JIT, EF, zimny HNSW — 422 ms vs 6,1 ms),
> druga runda gap-closingu, `augment`, rozszerzenie sąsiedztwa.
>
> **DECYZJA 2026-09-03: nie optymalizujemy teraz** — czasy uznane za zadowalające, a część
> infrastruktury i tak stoi lokalnie. Zapisane świadomie, nie przeoczone. Wrócić, gdy stack
> pojedzie w całości do chmury, bo wtedy te 15 s nie będzie już miało lokalnego alibi.
>
> **GOLDEN-SET, Przebieg A (router OFF), 2026-09-03 [zmierzone]** — `--chat`, 54 pozycje:
>
> | Metryka | Wynik |
> |---|---|
> | **Anty-halucynacja (pułapki)** | **100%** (5/5) |
> | **Abstynencja END-TO-END (LLM)** | **86%** (na 42 poz. z czatem) |
> | Trafność abstynencji (próg similarity) | 81% (na 54) |
> | Świeżość (nowela w źródłach) | 100% (1/1) |
> | Recall@K (retrieval) | 66% → **69%** po korekcie klucza (22/32) |
>
> **Werdykt: Gemma zdała.** Anty-halucynacja 100% to ta cecha, na której Bielik poległ. Pułapka
> `ue-trap-95-46` miała similarity 0,9226 (druga najwyższa w zestawie) i mimo to została odrzucona —
> bramka cytatów obroniła produkt przy maksymalnie pewnym i maksymalnie błędnym retrievalu.
>
> **Pomiar potwierdza decyzję projektową:** bramka LLM (86%) bije próg podobieństwa (81%),
> a kalibracja wskazuje najlepszy możliwy próg ≈0,30, który też daje tylko 81%. Trzymanie
> `AbstentionThreshold=0.00` + `CitationGateEnabled=true` jest wyborem lepszym od alternatywy.
>
> **KOREKTA 2026-09-04:** `uodo-60` miał nieaktualny klucz odpowiedzi — jego pomiar odniesienia
> pochodzi z 2026-08-11, a korpus UE (RODO `32016R0679`) wszedł do bazy dopiero **2026-08-27**.
> Przed tą datą jedynym prawem o ochronie danych była polska uodo, więc art. 60 „trafiał" z braku
> alternatywy. Oczekiwanie przeniesione na RODO art. 33 (zgłoszenie naruszenia w 72 h) —
> zweryfikowane, pozycja trafia. **Recall@K 66% → 69%.**
> Audyt reszty zestawu: tylko 2 pozycje-pudła mają notatki sprzed 27.08, z czego `ue-rodo-6` jest
> poprawna (pisana w antycypacji korpusu UE), a `uodo-107` jest czerwona z założenia. Problem
> nieaktualnego klucza okazał się **wąski, nie systemowy**.
>
> ⚠️ **Wąskim gardłem jest retrieval, nie model.** Recall@K 69% = w ~1/3 pytań właściwy przepis
> nie trafia do kontekstu w ogóle. To sufit jakości produktu — żaden LLM nie odpowie z materiału,
> którego nie dostał. Metryki retrievalowe nie zależą od LLM-a, więc ~21/32 wobec zapisanego
> baseline'u 19/32 **nie jest zasługą Gemmy**; źródła tej różnicy nie ustaliłem.
>
> Uboczne, warte zapamiętania: `InCorpus` (0,8458) i `OutOfCorpus` (0,8450) są po similarity
> **nierozróżnialne**, a pułapki mają średnią NAJWYŻSZĄ (0,8754). Cosine nie niesie sygnału
> o obecności odpowiedzi w korpusie — całe bramkowanie stoi na bramce cytatów, bez zapasowego
> mechanizmu. (Małe n: OutOfCorpus=2, Freshness=1 — kierunek, nie dowód.)
>
> **Wciąż otwarte w §0.2:** zużycie tokenów (do domknięcia kosztu w §5) i Przebieg B (router ON).

### 0.3 Zmniejsz bazę przed wysyłką — 106 GB → ~85 GB
Wysyłasz to raz, przez domowe łącze, i płacisz za provisioned storage. Rób to lokalnie, gdzie żaden
zegar nie tyka:

| Działanie | Zysk |
|---|---|
| Konwersja `Embedding` `vector(1024)` → `halfvec(1024)` (indeks HNSW już jest na cast) | **~16 GB** |
| `DROP TABLE reprocess_ustepy_backup, chunk_noise_backup, pilot_uopl_chunks_backup` | ~2 GB |
| `DROP INDEX IX_chunks_SearchVector_pl` (2,7 GB, 0 skanów) | 2,7 GB |
| `DROP INDEX IX_documents_CourtType, IX_documents_Status` (0 skanów), `IX_chunks_EmbeddedWith` (2 skany) | ~0,15 GB |

⚠️ `IX_chunks_SearchVector_pl` **zostaw**, jeśli planujesz przełączenie na `polish` — wtedy odbudowa
kosztuje więcej niż 2,7 GB miejsca. Sprawdź też, czy `feat/halfvec-retriever` już nie robi konwersji
kolumny; jeśli tak, to zadanie odpada.

### 0.4 Zmierz czas wysyłki (~85 GB po 0.3)
Nikt tego nie policzył, a to najbardziej prawdopodobne źródło niespodzianki w dniu wdrożenia.
Metoda: wyślij 5 GB na cokolwiek zdalnego, zmierz, ekstrapoluj. Jeśli wyjdzie więcej niż jedna doba
— to jest osobne zadanie do zaplanowania (dysk kurierem / etapowanie / restore z object storage),
nie coś do zrobienia „przy okazji".

### 0.5 Landing + dokumenty prawne
Potrzebne do wniosku (§3.2: URL strony + wideo pokazujące stronę i MVP) **i** do sprzedaży
kancelariom. Minimum: regulamin, polityka prywatności, umowa powierzenia (DPA) i **lista
podprocesorów**. Ta lista przy obecnym planie to: **Microsoft (Azure), CloudFerro, Resend, Stripe**.
Kupujący z kancelarii o nią zapyta — lepiej ją mieć spisaną niż składać w trakcie rozmowy handlowej.

### 0.6 Zmierz RTT do CloudFerro z Azure West Europe
Plan domyślnie trzyma GPU u CloudFerro, a bazę i aplikację na Azure (§1.5) — więc każda tura czatu
robi ~6–7 round-tripów przez WAN (patrz §1.5 — reranker pętli po batchach `MaxBatch=32`), a samego
RTT nie zmierzyłem.

Metoda, kosztuje grosze: postaw najtańszą VM w Azure West Europe na godzinę i odpal
`curl -w '%{time_total}'` na endpoint TEI u CloudFerro (`/health`, potem realny `/rerank`
z batchem 32 kandydatów, żeby uwzględnić transfer ~100 kB). Porównaj z punktem odniesienia
z sizing doca: embed 82–107 ms, reranker mediana ~480 ms, `retrieval.total` mediana 1,6 s.
Jeśli RTT ≈ 20–30 ms — ignoruj. Jeśli istotnie więcej — to argument za Azure GPU (§4.3),
ale wtedy z liczbą w ręku.

---

## 3. Faza 1 — długie lead-time'y, zależne od domeny, wciąż bez zegara Azure

### 1.1 Resend
Wymaga domeny i rekordów DNS (SPF/DKIM/DMARC) plus czasu na propagację i weryfikację. Zgodnie
z notatką projektową to **twardy blocker przed wyjściem do obcych** — dziś `Email:Provider` to
`"log"`. Uruchom i przetestuj realną wysyłkę (rejestracja, reset hasła, zaproszenia).
Do sprawdzenia: czy da się wymusić region EU dla przechowywania.

### 1.2 Stripe — aktywacja konta
Integracja jest już zrobiona i zweryfikowana na `.11` (spike płatności, dwa realne bugi znalezione
i naprawione). Zostaje **aktywacja konta firmowego**: dane spółki, konto bankowe, weryfikacja
tożsamości — to trwa dni i jest poza Twoją kontrolą, dlatego startuje tutaj, a nie w fazie budowy.
Produkty i ceny (`Billing:PriceId`) zakładaj teraz; klucze live i webhook podłączysz na produkcyjnym
URL w §5.6. Podmiot rozliczeniowy dla UE to Stripe Payments Europe (Irlandia) — to jest odpowiedź
na pytanie o jurysdykcję.

### 1.3 Nagraj wideo do wniosku (≤10 min)
Wymóg formalny: prezentacja pokazująca stronę i działający MVP. Możliwe dopiero po 0.5.

---

## 4. Faza 2 — wniosek do Microsoftu

### 2.1 Złóż wniosek
Portal: **https://foundershub.startups.microsoft.com** — logowanie przez LinkedIn, wypełnienie
~10 minut, rozpatrzenie **~3 dni robocze** **[zweryfikowane 2026-09-02]**.

Przygotuj wcześniej: URL strony (§0.1 + §0.5), jednoakapitowy opis problemu i rozwiązania, datę
założenia, informację o przychodach (albo „pre-revenue"), nazwiska i profile LinkedIn założycieli,
wideo z §1.3.

Kryteria kwalifikacji: spółka prywatna, for-profit, produkt software'owy, **poniżej 7 lat**, przed
rundą Series D, siedziba w kraju z dostępnym Azure. Wykluczone: agencje, software house'y,
konsultingi, kryptokoparki. **Uwaga na to wykluczenie** — opis firmy musi jasno pozycjonować Was
jako produkt SaaS, nie usługi.

Próg $5 000 nie jest w pełni samoobsługowy (w odróżnieniu od $1 000) — wymaga wykazania postępu
produktowego i trakcji. Macie działający produkt i testerów; to jest materiał do wniosku.

### 2.2 Po akceptacji — NIE aktywuj od razu
Zegar startuje przy aktywacji (§1.3). Aktywuj, gdy Faza 0 jest zamknięta i możesz budować w ciągu
kilku dni.

### 2.3 Złóż wniosek o quotę GPU — ale nie blokuj na nim
Seria NC wymaga osobnego wniosku o zwiększenie quoty, a na nowych subskrypcjach kredytowych bywa to
wolne albo odmawiane. Wniosek jest darmowy, więc złóż go od razu po aktywacji.

**Ale nie czekaj na odpowiedź.** Dzięki €250 od CloudFerro (§1.5) GPU stoi tam za darmo przez
~5 miesięcy — €0,246/h × ~195 h ≈ **€48/mies.** przy pracy 8–17 **[cena rynkowa, zweryfikowana
2026-09-02]** (nie €111 — to cena abonamentu miesięcznego). TEI ładuje model 1–2 min, więc cron
budzi maszynę o 7:45.

**Domyślnie startujemy na GPU CloudFerro.** Przejście na Azure GPU rozważ tylko wtedy, gdy quota
przyjdzie **i** zmierzona latencja cross-cloud (Azure West Europe ↔ Warszawa) realnie psuje
`retrieval.total`. To decyzja z pomiaru, nie z góry.

---

## 5. Faza 3 — budowa

Zasada nadrzędna: **Azure to głupi compute.** Zwykła VM + compose + reverse proxy. Żadnego App
Service, managed identity ani Azure-specyficznych bindingów. Wtedy wyjście (§7) to przeniesienie
kontenerów, a nie przebudowa. Wymóg hunspell (§1.4) i tak wymusza kontenerowego Postgresa
z `infra/Dockerfile.db`, co utrzymuje przenośność niejako przy okazji — nie psuć tego.

1. **VM** `E8ds_v5` (8 vCPU / 64 GB) + **Premium SSD v2** ~250 GB z 4000 IOPS (~$26/mies.;
   3000 IOPS jest w bazie za darmo, a v2 rozdziela IOPS od pojemności — stary P15 to ~1100 IOPS
   przy $34,56, czyli drożej i wolniej) **[cena rynkowa, zweryfikowana 2026-09-02]**.
   300 GiB lokalnego dysku efemerycznego **nie nadaje się na `PGDATA`**, ale jest darmowy i idealny
   na staging `pg_dump` i spill przy rebuildzie HNSW.
2. **compose**: Postgres z `infra/Dockerfile.db`, API, Umami. Caddy/nginx + Let's Encrypt na domenie.
3. **`DataProtection__KeysPath`** na trwałym dysku — na VM to poprawne i bardziej przenośne niż
   Key Vault (na App Service byłoby odwrotnie, ale tam nie idziemy).
4. **Upload + restore** wg pomiaru z §0.4.
5. **Tuning i weryfikacja.** Domyślne `shared_buffers` to ~25% RAM = 16 GB na 64 GB — **mniej niż
   samo HNSW (21 GB)**. Ustaw **`shared_buffers` 28–32 GB**, `effective_cache_size` ~48 GB.
   Weryfikacja: po rozgrzewce odpal **6 różnorodnych sond** (metoda z §1.6) i sprawdź, czy `read`
   spada w okolice zera. To jest dowód sizingu — nie specyfikacja z oferty.
   Ustaw też jawne limity pamięci kontenerów aplikacji i Umami, żeby nie zjadały page cache.
6. **Przełącz flagi** z `appsettings.json`: `Auth:Enabled`, `Billing:Enabled`, `Email:Provider`
   (`log` → `resend`), `Analytics:ScriptUrl`/`WebsiteId`, `Access:Enabled` wg strategii wejścia.
   Webhooki Stripe na produkcyjny URL, klucze live.
7. **`pg_basebackup` + archiwizacja WAL** do object storage. Restore ma być kopią plików
   **z gotowym indeksem**, nie rebuildem 12,5 h.
8. **GPU**: TEI (embedding) + reranker na osobnej maszynie GPU, harmonogram 8–17 (§1.2).

---

## 6. Faza 4 — próba generalna wyjścia (ok. 2. miesiąca kredytów)

Wynajmij **Hetzner AX42** (8 rdzeni / 64 GB / 2×512 GB NVMe, **€99/mies.** **[cena rynkowa,
zweryfikowana 2026-09-02]**) na jeden miesiąc i **odtwórz produkcję z backupu z §5.7**. Zweryfikuj,
że indeks HNSW przyjechał gotowy i że zapytania działają (znowu: 6 sond).

Koszt: €99 jednorazowo. Zysk: zamienia „migracja 106 GB pod presją handlową" w przećwiczoną
procedurę. To najtańsze ubezpieczenie w całym planie i jedyny punkt, w którym świadomie wydajesz
własne pieniądze w trakcie kredytów.

---

## 7. Faza 5 — wyjście z Azure

**Wyzwalacz: wyczerpanie kredytów (~9 miesięcy przy $550/mies.), nie data w kalendarzu.**

Cel: **Hetzner AX42 €99/mies. + GPU CloudFerro ~€48/mies. (8–17) ≈ €147/mies.** — wobec ~$550
na Azure. Procedura jest już przećwiczona w §6, więc to odtworzenie z backupu i przepięcie DNS.

Jeśli produkt na siebie nie zarabia wcześniej — ta sama procedura, tylko wcześniej. Nic w tym
planie nie zakłada, że musicie dotrwać do wyczerpania kredytów.

---

## 8. Co jest wciąż niesprawdzone

Trzy rzeczy, których nie mogłem zweryfikować, a które mogą zmienić plan:

1. ~~**Czy CloudFerro przyzna €250**~~ — **ZAMKNIĘTE 2026-09-03, przyznane** (§1.5). Ścieżka GPU
   u CloudFerro jest sfinansowana; quota GPU na Azure pozostaje wyłącznie preferencją (§4.3).
2. **Transze kredytów Azure** (§1.2) — jeśli $5 000 przychodzi etapami z osobnymi zegarami,
   harmonogram wymaga przeliczenia.
3. **Czas wysyłki ~85 GB** (§0.4) — jedyna pozycja, której rząd wielkości jest kompletnie nieznany,
   i jedyna, która blokuje podanie realnej daty releasu.
4. ~~**Quota GPU na Azure**~~ — **zdegradowane z bramki do preferencji** (§4.3). Dzięki €250 odmowa
   nic nie kosztuje przez ~5 miesięcy.

Oraz jedna niewiadoma produktowa, ważniejsza od wszystkich trzech: **`llm.total` i koszt tokenów
na realnej Gemmie z CloudFerro** (§0.2). Dopóki to jest puste, koszt zmienny produktu jest
nieznany, a jakość odpowiedzi w produkcji — niepotwierdzona.
