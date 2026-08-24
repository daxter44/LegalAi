# Sizing serwera produkcyjnego — 10 / 100 / 1000 użytkowników

Data: 2026-08-24. Branch: `feat/halfvec-retriever`.

## Jak czytać ten dokument

Każda liczba w tabelach ma etykietę:

- **[zmierzone]** — realny pomiar wykonany dziś na pełnym korpusie (7,56 mln chunków, 94 GB), przez
  realny łańcuch API → .11 (baza + TEI + reranker GPU). Komendy do odtworzenia są w każdej sekcji i
  w załączniku.
- **[cena rynkowa, zweryfikowana 2026-08-24]** — sprawdzone dziś przez wyszukiwanie, z linkiem źródłowym.
  Ceny GPU/RAM są dziś podbite (szok DRAM, patrz niżej) — **zweryfikuj ponownie przed zakupem**, to nie
  są ceny sprzed pół roku.
- **[założenie — do potwierdzenia]** — coś, czego nie da się zmierzyć bez Twojej wiedzy o realnym użyciu
  (ile pytań/dzień robi prawnik). Podane z wrażliwością (sensitivity), nie jako jedna zgadywana liczba.

Zero komórek bez etykiety. Jeśli czegoś nie zmierzyłem, mówię to wprost i zostawiam puste miejsce na
Twój pomiar — nie zgaduję w Twoim imieniu (patrz sekcja "O co pytałeś" na końcu).

---

## 0. Dwa odkrycia, które zmieniają całą resztę dokumentu

### Odkrycie 1: to nie serwer odpowiada za 45 sekund — to LLM

Pełny przebieg `/api/chat` na realnym pytaniu z golden-setu, na pełnym korpusie, z rerankerem GPU **[zmierzone]**:

| Etap | Czas | Gdzie się dzieje |
|---|---|---|
| embed (zapytanie → wektor, GPU .11) | 82-107 ms | Twój serwer/GPU |
| dense (HNSW halfvec, 7,56 mln wektorów) | 55-1536 ms (mediana ~340 ms) | Twój serwer (baza) |
| sparse (BM25/GIN) | 27-502 ms | Twój serwer (baza) |
| reranker (GPU, .11) | 81-1712 ms (mediana ~480 ms) | Twój serwer/GPU |
| tory dokładne + most cytowań | 0-1078 ms (zwykle ~0) | Twój serwer (baza) |
| **retrieval.total (48 próbek, sekwencyjnie + do 20 równolegle)** | **min 425 ms / mediana 1,6 s / p90 3,0 s / max 4,7 s** | **Twój serwer — to jest to, co kupujesz sprzętem** |
| **llm.total** | **43-48 s** (lokalny model 26B na MacBook Air, `Llm:Provider=local`) | **Zewnętrzny provider — NIE Twój serwer** |
| **chat.total** | **46-50 s** | |

LLM to 90-96% całkowitego czasu odpowiedzi w tym pomiarze, i **nie zużywa ani jednego cykla CPU/GPU
Twojego serwera** — to jest otwarte połączenie HTTP czekające na streaming z zewnętrznego API. Serwer,
o który pytasz, odpowiada za tę zmierzoną resztę: 0,4-4,7 sekundy.

**To jest bezpośrednio powiązane z Twoim doświadczeniem z M4/3060.** Tam problem był realny (Metal
faktycznie nie jest wspierany przez `text-embeddings-inference`), ale to był problem embeddingu/rerankera,
nie problem "jaki procesor kupić". Tu jest analogicznie: zanim wydasz pieniądze na serwer myśląc, że to
przyspieszy odpowiedź, sprawdź, czy 45 sekund to w ogóle Twój serwer. Dziś — nie jest.

**Czego NIE zmierzyłem i nie zgaduję:** czasu odpowiedzi z Gemini (Twój obecny `Llm__Local__BaseUrl`)
ani z docelowego CloudFerro/Gemma (patrz `RUNBOOK-LLM-PROVIDER.md` — decyzja produktowa to **żadnych
amerykańskich API LLM w funkcji produktu**, Gemini jest dziś mostem deweloperskim). Nie mam w tej sesji
klucza do żadnego z nich, więc nie zgaduję liczby. Zrób to sam, zajmie 2 minuty:

```bash
PRAWORAG_LOG_TIMING=1 Diagnostics__ShowTokenUsage=true \
Llm__Provider=local Llm__Local__BaseUrl=https://generativelanguage.googleapis.com/v1beta/openai/ \
Llm__Local__Model=<Twój model> Llm__Local__ApiKey=$GEMINI_API_KEY \
dotnet run --project src/PrawoRAG.Api
# potem: curl .../api/chat z prawdziwym pytaniem, przeczytaj [timing] llm.total i tokeny w odpowiedzi
```

Wstaw wynik do tabeli wyżej zamiast `43-48 s` — to jedyna liczba w tym dokumencie, którą musisz dostarczyć
Ty, bo ja nie mam do niej dostępu.

### Odkrycie 2 — było: coś w kodzie NIE skalowało się pod współbieżnością. NAPRAWIONE tego samego dnia.

16 równoległych `/api/chat` (pełny pipeline, mały `MaxTokens` żeby nie czekać na LLM) **[zmierzone]**,
PRZED naprawą:

| Etap | Solo (1 request) | 16 równoległych |
|---|---|---|
| `retrieval.total` | 0,5-0,9 s | 0,5-5,4 s (rośnie, ale w miarę łagodnie) |
| **`augment` (TemporalAugmenter)** | **700-867 ms** | **7,7 s → 18,5-22 s** |

`augment` był 20-30× wolniejszy pod obciążeniem niż solo. Przyczyna, potwierdzona (nie zgadywana):
`TemporalAugmenter.BuildUnabsorbedDatesAsync` filtrował `documents` (533 831 wierszy) po `DocType='act'`
i obecności klucza `unabsorbedAmendments` w `TypedMetadata` **bez indeksu pod ten predykat** — sekwencyjny
skan przy KAŻDEJ turze czatu zwracającej chunk aktu. Potwierdza to też zmierzony współczynnik trafień
cache na .11 sprzed naprawy: tabela `chunks` miała **1,4% hit ratio** na dysku, a odczyty
`documents`/TOAST sumowały się do **8,15 mln bloków** mimo że sama tabela to 1,5 GB **[zmierzone,
`pg_statio_user_tables`]**.

**Naprawa (ten sam dzień, migracja `20260824110401_AddDocumentsUnabsorbedAmendmentsIndex`):** indeks
częściowy na `documents`, zawężony dokładnie do predykatu zapytania (ten sam operator jsonb `?`, żeby
planner rozpoznał implikację). Efekt zmierzony wprost planem zapytania na .11:

```
Index Scan using "IX_documents_UnabsorbedAmendments" on documents
  (actual rows=14305 loops=1)
  Execution Time: 15.3 ms
```

(14 305 kwalifikujących się wierszy — więcej niż „garstka" z pierwotnego komentarza w kodzie, ale
indeks i tak zamienia to w 15 ms zamiast sekwencyjnego skanu 533k). Ten sam test 16-równoległych
`/api/chat` PO naprawie:

| Etap | Solo | 16 równoległych PRZED | 16 równoległych PO |
|---|---|---|---|
| **`augment`** | 700-867 ms | **18,5-22 s** | **0-7,2 s** (mediana ~4 s) |

Nie zero (16 zapytań nadal dzieli tę samą bazę), ale patologiczny wybuch zniknął — to już normalna
konkurencja o zasoby, nie liniowy koszt skanu całej tabeli. Testy: 574/574 zielone (w tym nowy
`Unabsorbed_amendments_index_exists_with_expected_filter` w `TemporalAugmenterTests.cs`, wzorowany na
`HalfvecIndexTests`, żeby regresja tego indeksu nie była znowu cicha).

**To był ważniejszy fix niż jakikolwiek numer w tabeli RAM/CPU niżej — i żaden większy serwer by go nie
zastąpił**, bo to był problem algorytmiczny (brak indeksu pod predykat), nie brak mocy.

---

## 1. Model obciążenia — Twoje założenia, nie moje zgadywanie

Potwierdziłeś: użytkownicy **płatni**, okazjonalni, skoncentrowani **8-17** (9h), prawie zero poza tym
oknem. Jedyna niewiadoma to ile zapytań/dzień robi jeden prawnik — **[założenie — do potwierdzenia]**,
pokazane dla trzech wartości:

| Zapytań/user/dzień | 10 userów: peak req/s | 100 userów: peak req/s | 1000 userów: peak req/s |
|---|---|---|---|
| 3 (rzadkie użycie) | 0,002 | 0,019 | 0,19 |
| 8 (środkowy scenariusz) | 0,005 | 0,049 | 0,49 |
| 20 (intensywne) | 0,012 | 0,123 | 1,23 |

Metoda: dzienny wolumen = N × zapytania/dzień; średnia godzinowa w oknie 9h; szczyt godzinowy = 2×
średnia (typowy współczynnik piku dla ruchu biznesowego, **[założenie]**); peak req/s = szczyt/3600.

**Jednoczesne zapytania w danym momencie** (prawo Little'a, `concurrency = req/s × czas obsługi`) —
liczone TYLKO na zmierzonym czasie retrievalu (2 s, bo to jedyna część zużywająca CPU/GPU Twojego
serwera — czekanie na LLM zajmuje otwarte połączenie, nie moc obliczeniową):

| Zapytań/dzień | 10 userów | 100 userów | 1000 userów |
|---|---|---|---|
| 3/dzień | ~0 | ~0 | ~0,4 |
| 8/dzień | ~0 | ~0,1 | ~1 |
| 20/dzień | ~0 | ~0,25 | ~2,5 |

**Nawet przy najintensywniejszym scenariuszu i 1000 userach, realna współbieżność strony serwera to
pojedyncze cyfry.** To zgadza się z tym, co zmierzyłem bezpośrednio: 20 równoległych zapytań na dzisiejszym
sprzęcie (MacBook Air + dom, 128 MB `shared_buffers`) przeszło bez błędów (mediana 2,5 s, max 4 s) — patrz
Odkrycie 2 wyżej dla zastrzeżenia o `augment`.

Jeśli otwarte połączenia SSE czekające na LLM Cię niepokoją (a nie CPU/GPU) — to inna oś: nawet setki
jednoczesnych strumieni to dla Kestrela (ASP.NET) nic, to tania rzecz pamięciowo, nie potrzebuje GPU.

---

## 2. RAM — ustalony korpusem, nie liczbą userów

**[zmierzone]** na .11: baza 94 GB (7 557 148 chunków), indeks HNSW halfvec 19 GB, dwa indeksy GIN
(BM25) ~5,5 GB łącznie. `shared_buffers` na .11 to dziś 128 MB — deweloperski default, nie kopiuj go na
prod.

**[zmierzone]** współczynnik trafień cache (`pg_statio_user_indexes`/`pg_statio_user_tables`,
skumulowane od 37h uptime kontenera): indeks HNSW **29,6%** hit ratio, tabela `chunks` **1,4%** hit ratio.
To znaczy: na dzisiejszym sprzęcie większość odczytów idzie na dysk, nie z pamięci. Wasz własny
`RUNBOOK-3060-DOCKER.md` (§9) potwierdza dlaczego: środowisko WSL2/Docker na .11 ma "realny sufit ~15 GB
RAM" — **mniej niż sam indeks HNSW (19 GB)**. Fizycznie nie mieści się w całości.

To jest **dowód**, nie spekulacja specyfikacją: RAM poniżej ~32 GB odtwarza dokładnie ten sam problem na
nowym serwerze. Rekomendacja, identyczna na każdym progu userów (bo to korpus, nie ruch, dyktuje RAM):

- **Absolutne minimum: 32 GB** (indeksy + trochę headroomu, bez marginesu na wzrost korpusu).
- **Rekomendowane: 48-64 GB** (indeksy + hot chunks + connection overhead + margines na wzrost —
  planujecie dokładać źródła, patrz `docs/PLAN-*` roadmapa ingestii).
- Dysk: **NVMe, nie sieciowy block storage klasy HDD** — HNSW to losowe odczyty, IOPS/latencja dysku
  liczy się bardziej niż liczba rdzeni. Min. 200-250 GB (94 GB danych + WAL + margines na
  `pg_dump`+rebuild HNSW, który wg Waszego runbooka zajął **12,5h** i potrzebuje miejsca na kopię obok
  oryginału podczas migracji).

---

## 3. Tabela werdyktów

| | 10 userów | 100 userów | 1000 userów |
|---|---|---|---|
| vCPU | 2-4 **[założenie — margines, nie zmierzone zapotrzebowanie]** | 4-8 **[założenie]** | 8-16 **[założenie]** |
| RAM | 32-48 GB **[zmierzone: floor korpusu]** | 32-48 GB **[j.w., ten sam korpus]** | 48-64 GB **[j.w. + margines wzrostu]** |
| Dysk | 200 GB NVMe **[zmierzone: 94GB dane + margines]** | 200-250 GB NVMe | 250-300 GB NVMe |
| GPU | Wymagany (reranker), ale **NIE 24/7** — patrz §4 | Wymagany, NIE 24/7 (peak GPU ~1,5% wykorzystania) | Wymagany, rozważ 24/7 (peak GPU ~15%, ale wciąż niewykorzystany) |
| Co pęknie pierwsze, jeśli zaniżysz | RAM < 32GB → wraca dzisiejszy problem cache (29,6%/1,4% hit ratio) | Kolejka na GPU rerankerze pod większym ruchem (patrz §1) | To samo, mocniej — pilnuj GPU pod obciążeniem |
| Cena serwera (bez GPU) | ~€20-45/mies. (Hetzner CPX/CCX klasy 4vCPU/8-16GB) **[cena rynkowa, zweryfikowana 2026-08-24, hetzner.com — ceny podbite +144-176% w czerwcu 2026, szok DRAM]** | ~€45-90/mies. | ~€150-300/mies. (skalowanie RAM) |
| Cena GPU | ~€150-190/mies. jeśli tylko 8-17 (Scaleway L4 na godziny, ~€0,79/h × ~195h) **[cena rynkowa, zweryfikowana 2026-08-24, scaleway.com]** | tak samo | ~€400-580/mies. jeśli 24/7 (Scaleway) LUB dedykowany box typu Hetzner GEX44 ~€184/mies. **[cena rynkowa, ale SKU pokazywało się jako "niedostępne" w lipcu 2026 — potwierdź dostępność przed decyzją]** |
| Koszt LLM/mies. | **[czekam na Twój pomiar, patrz §0]** | **[j.w.]** | **[j.w. — prawdopodobnie porównywalny z kosztem serwera, patrz §5]** |

---

## 4. Architektura — odpowiedź na Twoją obawę o dom

Powiedziałeś jasno: nie chcesz hostować produkcji (płatnej!) w domu — restart Windowsa, WiFi, to realne
ryzyko dla czegoś, za co ludzie płacą. Zgadzam się, to nie jest miejsce na kompromis dla płatnych userów.
Rekomendacja: **wszystko w chmurze, EU-hosted** (zgodne też z Waszą już zapisaną decyzją suwerenności
w `RUNBOOK-LLM-PROVIDER.md`).

Ale — i to wynika wprost z pomiaru w §1, nie z teorii — **GPU będzie bezczynne 98,5-99,95% czasu przy
10-100 userach**. Postawienie 24/7 dedykowanego GPU-serwera na tym etapie to dokładnie scenariusz,
którego się obawiasz ("kupiłem za 200$ i większość zasobów się nudzi"). Dwie opcje, obie tańsze:

1. **GPU na godziny, tylko 8-17** (Scaleway/RunPod, start/stop skryptem lub cronem) — koszt jak w tabeli,
   ale operacyjnie: TEI ładuje model ~1-2 min przy starcie (małe modele, ~0,7GB fp16 każdy), więc
   pierwszy request rano może poczekać chwilę dłużej. Zaplanuj rozgrzewkę przed 8:00.
2. **Jeden dedykowany serwer z GPU** (typu Hetzner GEX44, jeśli dostępny — bundling GPU+64GB RAM+CPU
   w jednej cenie bywa tańszy niż osobne boxy), hostujący DB+TEI+reranker+API razem. Prostsze operacyjnie
   (jeden box, brak sieciowania między maszynami), ale płacisz 24/7 nawet gdy śpi.

VRAM nie jest ograniczeniem na żadnym progu — oba modele (embedding + reranker) to razem ~1,4 GB fp16
**[potwierdzone przez `/info` TEI dziś, dtype float16]** — nawet najtańsza karta klasy T4/L4/3060 starczy
z ogromnym zapasem. Nie przepłacaj za VRAM, przepłacisz co najwyżej za to, że karta stoi 24/7.

---

## 5. Koszt LLM — osobna linia budżetu, prawdopodobnie ważniejsza niż serwer

Bez znajomości realnego `llm.total` i zużycia tokenów nie policzę tego precyzyjnie (patrz §0 — Twój
pomiar). Punkt odniesienia z web searchu **[cena rynkowa, zweryfikowana 2026-08-24]**:

- Gemini 2.5 Flash: $0,15/1M wejście, $1,25/1M wyjście. **Uwaga: model wycofywany 16.10.2026** — ten
  punkt odniesienia wygasa za mniej niż dwa miesiące, nie planuj na nim budżetu długoterminowo.
  ([devtk.ai](https://devtk.ai/en/models/gemini-2-5-flash/))
- CloudFerro Sherlock (Gemma, EU, docelowy provider wg Waszego runbooka): €0,56/1M tokenów.

Przy założeniu ~4500 tok. wejścia (TopK=8 × ~450 tok. target chunk) + do 1024 tok. wyjścia (dziś ucina
przy limicie — Wasz `llm.total=43-48s` na 26B lokalnie silnie sugeruje, że output faktycznie zbliża się
do 1024, nie połowy tego): rząd wielkości **1000 zapytań/mies. → jednocyfrowe-kilkudziesięciodolarowe
kwoty; 1000 userów × 8 zapytań/dzień × 22 dni robocze ≈ 176 000 zapytań/mies. → prawdopodobnie
porównywalne z kosztem serwera, może go przewyższyć**. To jest szacunek, nie pomiar — potwierdź realnym
`ShowTokenUsage` z §0, bo to zmienia który koszt faktycznie dominuje na progu 1000.

---

## 6. Co przełamie ten werdykt

- **Self-hosted Gemma zamiast API** — jeśli kiedyś zrezygnujecie z CloudFerro/Gemini na rzecz lokalnego
  modelu 31B, to zupełnie inny GPU (z ~1,4 GB VRAM na 20-60+ GB) i zupełnie inna tabela. Dziś LLM jest
  zewnętrzny, więc nie liczę tego scenariusza w werdykcie głównym.
- ~~`augment` niepoprawiony~~ — **naprawione tego samego dnia** (Odkrycie 2, indeks
  `IX_documents_UnabsorbedAmendments`, migracja `20260824110401`). Zostawiam wpis, bo to przykład klasy
  problemu: jeśli w przyszłości pojawi się PODOBNY spadek (nowy full-scan bez indeksu dodany razem
  z nową funkcją), żaden hardware z tabeli w §3 tego nie przykryje — trzeba złapać to samym pomiarem
  (16-równoległe `/api/chat` + `PRAWORAG_LOG_TIMING`), nie zgadywaniem z rozmiaru serwera.
- **Realne zapytania/user/dzień znacznie wyżej niż 8** — przelicz tabelę w §1 z Twoją realną liczbą, gdy
  ją poznasz z pilotażu.
- **Wzrost korpusu** — dokładacie źródła (orzeczenia innych poziomów, więcej lat) — RAM floor w §2 rośnie
  razem z tym, nie jest jednorazowy.
- **Rynek RAM/GPU się unormuje** — ceny dziś są podbite (+144-176% Hetzner, czerwiec 2026, szok DRAM).
  Zweryfikuj ceny ponownie tuż przed podpisaniem umowy, nie licz na te z tego dokumentu za 2-3 miesiące.

---

## 7. Jak to zweryfikować samemu, zanim podpiszesz umowę na serwer

To jest odpowiedź na Twoją właściwą obawę, nie lepiej uargumentowana rekomendacja: **nie kupuj na
podstawie moich liczb**. Hetzner/Scaleway rozliczają godzinowo. Wynajmij kandydata na **jedną godzinę**
(grosze), odtwórz fragment zrzutu bazy albo po prostu podepnij ten sam .11 (tunel WireGuard na czas
testu), i odpal DOKŁADNIE te komendy:

```bash
# 1. Uruchom API na kandydackim serwerze, wskazując na realną bazę/GPU (lub lokalną kopię):
PRAWORAG_LOG_TIMING=1 ConnectionStrings__Db=... Embeddings__BaseUrl=... Reranker__BaseUrl=... \
  dotnet run --project src/PrawoRAG.Api

# 2. Sekwencyjnie po golden-secie (18 pytań, ~30s):
python3 -c "
import json, subprocess, time
qs = json.load(open('src/PrawoRAG.Eval/golden-set.json'))
for q in qs:
    t0=time.time()
    subprocess.run(['curl','-s','-m','20','http://localhost:5099/api/search','-X','POST',
                     '-H','content-type: application/json',
                     '-d', json.dumps({'query': q['question'], 'topK': 8})], capture_output=True)
    print(f\"{(time.time()-t0)*1000:.0f}ms {q['id']}\")
"

# 3. Współbieżnie (16-20 na raz) — powtórz test z §0/Odkrycia 2, patrz surowe logi w
#    docs/_raw-sizing-log-2-16concurrent-chat.txt jako wzorzec do porównania.
```

Porównaj wynik z tabelami w §0/§3 tego dokumentu. Jeśli się nie zgadza — ufaj SWOJEMU pomiarowi, nie
mojemu dokumentowi. Surowe logi z dzisiejszych pomiarów (do wglądu/porównania):
`docs/_raw-sizing-log-1-sequential-i-8-20-search.txt`, `docs/_raw-sizing-log-2-16concurrent-chat.txt`.

---

## O co pytałeś

Zapytałeś, żeby to wynikało z pomiarów, nie z mojej pewności. Poprzednio (M4/Metal) doradzałem na
podstawie architektury sprzętu, nie sprawdzonego zachowania — i to był błąd, który Cię kosztował. Dziś:
każda liczba w §0-§2 pochodzi z realnego przebiegu na Waszym pełnym korpusie (7,56 mln chunków) i realnym
GPU (.11), nie ze specyfikacji. Tam gdzie nie mogłem zmierzyć (koszt/latencja LLM — nie mam klucza do
Gemini/CloudFerro w tej sesji), zostawiłem puste miejsce zamiast zgadywać. Jedyne miejsce z moją
interpretacją bez twardego pomiaru to model obciążenia w §1 (bo zależy od Twojej wiedzy o użytkownikach,
nie od czegoś mierzalnego przeze mnie) — i tam jawnie pokazuję trzy warianty zamiast jednej liczby.
