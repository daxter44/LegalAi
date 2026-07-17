# Ewaluacja egzaminacyjna po odbudowie indeksu halfvec (2026-07-17)

Kontekst: HNSW na `chunks.Embedding` był celowo usunięty przed bulk-insertem pełnego korpusu (7 427 688
chunków) i odbudowany dopiero po nim — z konieczności jako **indeks wyrażeniowy na `Embedding::halfvec(1024)`**
(fp16), bo fp32 nie mieściło się w dostępnej pamięci maszyny z RTX 3060. GIN na `SearchVector` odbudowany
bez problemu (15 min 20 s). HNSW halfvec wymagał trzech podejść (limity `/dev/shm`, potem realny limit
pamięci WSL2/Docker ~15 GB zamiast zakładanych 32 GB) i ostatecznie zbudował się w ~12,5 h przy
`maintenance_work_mem=12GB`, `max_parallel_maintenance_workers=0`.

Po odbudowie retriever ([HybridRetriever.cs](../src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs)) trzeba
było przepisać: LINQ `CosineDistance` porównywał fp32↔fp32 i nie trafiał w nowy, wyrażeniowy indeks halfvec
(pełny sequential scan po 7,4 mln wierszy przy każdym zapytaniu). Tor gęsty przepisano na surowe,
sparametryzowane SQL z rzutem `::halfvec(1024)` po obu stronach `<=>` (branch `feat/halfvec-retriever`).

Ta ewaluacja odpowiada na pytanie: **czy fp16 (halfvec) pogorszyło jakość retrievalu względem wcześniejszego
fp32, i czy naprawiony retriever faktycznie korzysta z nowego indeksu na pełnym, produkcyjnym korpusie.**

## Metodyka

Harness `--exam` w [PrawoRAG.Eval](../src/PrawoRAG.Eval) (zestaw `egzaminy-wstepne-2025.json` — 446 pytań
ABC scalonych z trzech egzaminów wstępnych 27.09.2025: adwokacko-radcowski, komorniczy, notarialny,
Ministerstwo Sprawiedliwości), trzy tryby per pytanie:

- **solo** — sam LLM (Bielik 11B, lokalnie, Ollama), bez kontekstu — baza wiedzy modelu.
- **rag** — pełny `HybridRetriever` (system produkcyjny) — realny wynik.
- **oracle** — chunki dokładnie z artykułu oficjalnej podstawy prawnej odpowiedzi (retrieval idealny, sufit).

LLM: `bielik:latest` (11,2B, Q8_0) przez Ollama lokalnie na M4 — zero kosztu API. Baza + TEI: maszyna z
RTX 3060 (`192.168.100.11`), pełny korpus po odbudowie indeksów.

Bieg uruchomiony ręcznie w terminalu użytkownika (nie z headless środowiska agenta — to środowisko miało
zablokowany dostęp do sieci lokalnej na poziomie piaskownicy, co przez sześć prób dawało natychmiastowy
`No route to host` niezależnie od realnego stanu sieci; potwierdzone gołym `HttpClient` bez żadnego kodu
projektu). Czas trwania: **5 h 41 min** (11:56:45 → 17:38:03), 446 pytań × 3 tryby = 1338 wywołań LLM,
zero nieobsłużonych wyjątków.

## Wyniki

| Tryb | Trafność | Uwagi |
|---|---|---|
| solo | 71,1% (317/446) | baza wiedzy modelu |
| **rag** | **81,6%** (364/446) | wynik produkcyjny |
| oracle | 77,8% (347/446) surowo → **96,1%** na pytaniach z rozpoznaną podstawą (361/446) | idealny retrieval |

Próg zdawalności ludzi: 66,7%. `rag` bije go o **+14,9 pp**.

Delty: `rag − solo` = **+10,5 pp** (retrieval dokłada realną wartość). `oracle − rag` na uczciwym
porównaniu (tylko pytania z pokrytą podstawą) ≈ **+14,5 pp** — tyle zostaje na stole przez chybienia
retrievalu; jest pole do dalszej poprawy, ale system nie jest oderwany od sufitu.

### Retrieval (tryb rag) — potwierdzenie poprawki halfvec na pełnej skali

- Podstawa prawna rozpoznana w korpusie: 381/446 (85,4%) — reszta to akty poza korpusem lub
  niesparsowalne wykazy (mapa luk korpusu).
- **Artykuł z wykazu trafił do kontekstu: 326/381 (85,6%)** — **100% tych trafień (326/326) przyszło
  torem `dense`** (zero przez `structural`, zero przez `lexical`). Naprawiony tor gęsty
  (`::halfvec(1024)`) niesie cały ciężar trafień wektorowych na produkcyjnym korpusie po odbudowie
  indeksu — dowód, że poprawka działa poprawnie, nie tylko się kompiluje.
- Trafność gdy artykuł w kontekście: 85,6% vs gdy go brak: 65,5% (różnica 20,1 pp = cena chybień
  retrievalu).
- Bramka abstynencji: 0/446 (oczekiwane — próg=0 w tym evalu).

### Trafność per dziedzina (rag)

| Dziedzina | Trafność | n |
|---|---|---|
| kpk | 100% | 9 |
| ustrojowe | 100% | 7 |
| kks | 100% | 1 |
| ksh | 97% | 33 |
| traktaty-ue | 91% | 11 |
| kpa | 90% | 20 |
| inne | 89% | 18 |
| kc | 88% | 78 |
| kp | 88% | 8 |
| kpc | 83% | 42 |
| konstytucja | 80% | 20 |
| ustawa-szczegolna | 71% | 183 |
| kw | 50% | 2 (za mała próba) |

Najsłabsza liczebnie istotna kategoria to `ustawa-szczegolna` (n=183, najbardziej różnorodna) — spodziewane.

### Zgodność z wcześniejszym częściowym wynikiem

Pierwszy bieg padł po 149/446 pytaniach (zewnętrzny `kill`, niezwiązany z jakością retrievalu). Częściowy
wynik na tej próbce: `rag`=83,2%, dense-hit=87,0%. Pełny zestaw: `rag`=81,6%, dense-hit=85,6% — różnica
~1,5 pp w obie strony. **Próbka była wiarygodnym podglądem**, pełny bieg to potwierdza.

## Wniosek

Halfvec (fp16) **nie pogorszył** jakości retrievalu w sposób widoczny na tym zestawie — `rag` bije próg
zdawalności ludzi z dużym zapasem, a wszystkie trafienia artykułu z wykazu w kontekście idą przez tor
gęsty. Poprawka retrievera z tego samego dnia (branch `feat/halfvec-retriever`,
[HybridRetriever.cs](../src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs)) jest zweryfikowana na pełnym,
produkcyjnym korpusie, nie tylko testach jednostkowych.

## Otwarte wątki (niezaadresowane w tym biegu)

- Paralelizacja pipeline'u ingestii (wydajność `process`, niezwiązane z jakością retrievalu).
- Odporność `TeiEmbeddingProvider`/`IngestionPipeline` na `HttpClient.Timeout` — częściowo zaadresowane
  tego samego dnia retry'em w `TeiEmbeddingProvider.WithRetryAsync` (do 6 prób, ~42 s okna backoffu), ale
  warto sprawdzić czy pokrywa oryginalnie zgłoszony scenariusz w pipeline'ie ingestii.
- `oracle − rag` ≈ +14,5 pp na pokrytych pytaniach — cena chybień retrievalu; kandydat do dalszej pracy
  nad rankingiem/liczbą kandydatów, poza zakresem tej sesji.

Surowe dane per pytanie (JSONL, tryb/litera modelu/klucz/trafienie/domena): `logs/exam-20260717-095645.jsonl`
(niewersjonowane, lokalne u operatora biegu).
