# Pomiar „PO" — prawo UE w korpusie (transza T1, 2026-08-27)

Kontynuacja [POMIAR-PRAWO-UE-PRZED.md](POMIAR-PRAWO-UE-PRZED.md) i
[RUNBOOK-INGESTIA-UE.md](RUNBOOK-INGESTIA-UE.md). Transza T1 (`EurLex:Discover:YearFrom=2016`,
978 aktów z treścią) zaingestowana w całości do bazy na `192.168.100.11`:
**fetch 978/978 (973 pobrane, 5 już w magazynie, 0 błędów), process 978/978 (975 wstawione,
3 pominięte jako już zaindeksowane, 0 błędów)**.

Po drodze naprawiony realny bug w `EurLexConnector` (commit `e3c5b4f`): CELLAR odpowiada
przekierowaniem 303 z downgrade'em protokołu (https→http), którego domyślny `HttpClient` w .NET
celowo nie podąża automatycznie — bez ręcznej obsługi KAŻDE pobranie z CELLAR-a kończyło się
błędem, niezależnie od maszyny. Naprawione i zweryfikowane przed pełnym przebiegiem.

`golden-set.json`: pozycja `out-rodo` zmieniona z `OutOfCorpus`/odmowa na `InCorpus` z
`expectedEli: "32016R0679"` — zgodnie z notatką zostawioną w Fazie 0 (RODO teraz w korpusie).

## 1. Wynik zbiorczy — PRZED vs PO

| metryka | PRZED (14/40 pozycji, 2026-08-26) | PO (T1, 2026-08-27) | delta |
|---|---|---|---|
| Recall@K (retrieval) | 26% (7/27 poz. z oczek. źródłem) | **71% (20/28)** | **+45 pkt proc.** |
| Trafność abstynencji | 72% (na 40) | 75% (na 40) | +3 pkt proc. |
| Świeżość (Freshness) | 0% (1 poz.) | 0% (1 poz.) | bez zmian (niepowiązane z UE) |
| Śr. similarity w korpusie | 0,833 | 0,844 | +0,011 |
| Śr. similarity poza korpusem | 0,849 | 0,855 | +0,006 |
| Rozdział (in − out) | −0,016 | −0,011 | nadal brak czystego progu cosine (oczekiwane — bramka jest po stronie LLM/`--chat`) |

## 2. Pozycje `ue-*` merytoryczne (18 pozycji, `InCorpus`) — z zera do 13/18

PRZED: **0/18** (żadna nie miała czego trafić — korpus nie zawierał prawa UE).
PO: **13/18 (72%)**.

| pozycja | akt (CELEX) | wynik |
|---|---|---|
| `ue-rodo-6` | 32016R0679 art. 6 | ✅ hit |
| `ue-rodo-33` | 32016R0679 art. 33 | ✅ hit |
| `ue-rodo-17` | 32016R0679 art. 17 | ❌ miss |
| `ue-aiact-5` | 32024R1689 art. 5 | ✅ hit |
| `ue-aiact-50` | 32024R1689 art. 50 | ❌ miss |
| `ue-aiact-deepfake` | 32024R1689 art. 5 lit. ba) (tylko w konsolidacji) | ✅ hit |
| `ue-dsa-16` | 32022R2065 art. 16 | ❌ miss |
| `ue-dma-5` | 32022R1925 art. 5 | ✅ hit |
| `ue-konsument-9` | 32011L0083 art. 9 | ✅ hit |
| `ue-kierowcy-6` | 32006R0561 art. 6 | ✅ hit |
| `ue-zywnosc-9` | 32011R1169 art. 9 | ✅ hit |
| `ue-mdr-10` | 32017R0745 art. 10 | ❌ miss |
| `ue-produkty-5` | 32023R0988 art. 5 | ✅ hit |
| `ue-mar-17` | 32014R0596 art. 17 | ✅ hit |
| `ue-turystyka-12` | 32015L2302 art. 12 | ✅ hit |
| `ue-dsm-17` | 32019L0790 art. 17 | ✅ hit |
| `ue-reach-33` | 32006R1907 art. 33 (strażnik: konsolidacja przed bazowym) | ✅ hit |
| `ue-eprivacy-5` | 32002L0058 art. 5 (strażnik: markup legacy bez kotwic) | ❌ miss |

Dwa strażniki mechanizmu (nie tylko treści) przeszły: `ue-aiact-deepfake` (tekst skonsolidowany
faktycznie ingestowany) i `ue-reach-33` (konsolidacja poprawnie priorytetyzowana nad tekstem
bazowym). Trzeci, `ue-eprivacy-5` (tor parsowania bez kotwic id="art_*"), **nie przeszedł** — spójne
z raportem jakości (Krok 3 runbooka), gdzie ten sam wzorzec „Markup legacy" pojawił się jako
najniższe ryzyko strukturalne w próbce. 5 miss to kandydaci do dalszej diagnozy (`--probe-chunk`),
nie dowód złej ingestii — mogą to być zwykłe przegrane rankingiem, jak w innych diagnozach tej sesji.

## 3. Pozycje odmowowe `ue-*` (4 pozycje: `Trap`/`OutOfCorpus`/`RelatedButWrong`)

Wszystkie cztery (`ue-trap-95-46`, `ue-trap-rodo-999`, `ue-out-ccpa`, `ue-related-ukgdpr`) mają
`hit=—` (poprawnie — nie mają oczekiwanego źródła, oceniane przez trafność abstynencji/similarity,
nie przez recall). Similarity tych pozycji pozostaje wysoka (0,80–0,90) tak jak przed ingestią —
zgodnie z oczekiwaniem, bo to pułapki BEZ poprawnej odpowiedzi w korpusie, ingestia UE nie miała
prawa tego zmienić.

## 4. Regresja polska (9 pozycji spoza `ue-*` z oczekiwanym źródłem) — ⚠ WYMAGA WERYFIKACJI

PRZED: zbiorczo 7/27 trafień, wszystkie polskie (bramka Fazy 0 potwierdziła 0 trafień UE) → **7/9**
pozycji polskich, z czego `uodo-107` to świadomie oczekiwany miss (zapisane w PRZED jako „świadomie
czerwona").

PO: policzone wprost z tego przebiegu:

| pozycja | wynik PO |
|---|---|
| `kk-148` | ✅ hit |
| `kp-52` | ✅ hit |
| `kc-415` | ✅ hit |
| `kk-278` | ✅ hit |
| `kro-rozwod` | ✅ hit |
| `konsument-odstapienie` | ✅ hit |
| `kpk-41` | ❌ miss |
| `uodo-60` | ❌ miss |
| `uodo-107` | ❌ miss (oczekiwane, znane) |

To **6/9**, czyli o jedno trafienie mniej niż arytmetyka PRZED sugeruje (7/9). **Nie wiadomo z tego
przebiegu, czy to `kpk-41` czy `uodo-60` był wcześniej trafieniem** — PRZED zapisano tylko zbiorczy
procent i ranking similarity (`EWALUACJA-GOLDEN-SET-BASELINE-PRZED-EURLEX-2026-08-26.md`), nie
per-pozycję `hit=True/False` na poziomie pliku. To luka w moim własnym zapisie baseline, nie w
danych zespołu — do naprawienia przy następnym pomiarze (zapisywać hit per pozycję, nie tylko
zbiorczy procent).

**POTWIERDZONE `--probe-chunk` (2026-08-27), NIE jest to już hipoteza:**

| pozycja | chunk celu | exact fp32 rank | HNSW rank | sim | `CandidatesPerPath` (produkcja) |
|---|---|---|---|---|---|
| `kpk-41` | KPK art. 41 §2 | **#67** | #65 (zgodny) | 0,8147 | **50** |
| `uodo-60` | UODO art. 60 | **#88** | #82 (zgodny) | 0,8266 | **50** |

Dla OBU pozycji: HNSW jest zgodny z dokładnym skanem (indeks działa poprawnie, nie gubi wektora),
embedding dobrze reprezentuje przepis (similarity 0,81–0,83, nie jest to rozmycie chunka) — po
prostu oba chunki lądują TUŻ ZA twardą granicą `CandidatesPerPath=50` (`Retrieval.cs:56`,
`Program.cs:406` w API — ten sam parametr, dzielony z produkcyjnym czatem, nie tylko evalem). BM25
też nie ratuje (tsquery z AND-em wszystkich słów pytania nie matchuje żadnego z dwóch chunków —
znany, osobny wzorzec „Case 4").

To jest dokładnie mechanizm R7 z `PLAN-PRAWO-UE.md`: **stały rozmiar okna kandydatów (50) nie
skaluje się z rosnącym korpusem.** ~975 nowych dokumentów UE (realnie dziesiątki tysięcy nowych
chunków, sądząc po raporcie jakości z Kroku 3 — średnio 150 segmentów/akt) dodało konkurentów do
tej samej, niezmienionej puli top-50 gęstego wyszukiwania. Oba przepisy nie zniknęły z korpusu ani
nie pogorszył się ich embedding — zostały wypchnięte przez samą liczbę nowych kandydatów.

**To NIE jest coś, co ustąpi samo — T2 (kolejna transza, porównywalna lub większa objętościowo)
pogłębi ten sam efekt na kolejnych granicznych przypadkach**, nie tylko na tych dwóch już złapanych.

## 5. Bramka T2 (na podstawie tego pomiaru)

- **UE strona: zaliczona bez zastrzeżeń.** 0→13/18 merytorycznych, oba strażniki mechanizmu
  (`ue-aiact-deepfake`, `ue-reach-33`) przeszły. Ingestia ma mierzalny sens.
- **Polska strona: POTWIERDZONA regresja, mechanizm zidentyfikowany (R7 — rozcieńczenie okna
  kandydatów).** Nie szum pomiaru — `--probe-chunk` pokazuje spójny wzorzec na obu pozycjach
  (patrz sekcja 4). To realny koszt każdej kolejnej transzy przy dzisiejszej konfiguracji
  `CandidatesPerPath=50`, nie jednorazowy wypadek.

**Decyzja do podjęcia PRZED T2 (nie techniczna, biznesowa/priorytetowa):**
1. **Podnieść `CandidatesPerPath`** (np. 50→100) i zmierzyć koszt — to wspólny parametr z
   produkcyjnym czatem (`ChatService`/`Program.cs` w API), więc zmiana wpływa na WSZYSTKIE
   zapytania, nie tylko na eval. Koszt do zmierzenia: narzut na `rerank.main` (dziś ~400–700 ms
   przy k=50 na tor, z logów `PRAWORAG_LOG_TIMING` tej sesji) — podwojenie puli prawdopodobnie
   podwaja też ten narzut. Nie zgadywać, zmierzyć przed wdrożeniem (zasada tego projektu).
2. **Zaakceptować ryzyko i iść w T2**, wiedząc że każda transza będzie po cichu spychać kolejne
   graniczne polskie przepisy poza okno — bez własnego mechanizmu wykrywania tego na bieżąco
   (dziś złapane tylko dlatego, że te dwie pozycje akurat są w golden-secie).
3. **Wstrzymać T2**, dopóki (1) nie zostanie zrobione i zmierzone.

Bramka `UE-5.3` w obecnym stanie: **NIE spełniona bez decyzji o (1)** — regresja jest realna i
zidentyfikowana, ale jej rozwiązanie (podniesienie `CandidatesPerPath`) nie zostało jeszcze ani
wdrożone, ani zmierzone kosztowo.
