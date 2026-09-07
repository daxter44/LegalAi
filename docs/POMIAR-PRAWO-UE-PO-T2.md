# Pomiar „PO" — prawo UE w korpusie (transza T2, 2026-08-27)

Kontynuacja [POMIAR-PRAWO-UE-PO.md](POMIAR-PRAWO-UE-PO.md) (T1). Transza T2 (`YearFrom=2004,
YearTo=2015`, 1953 aktów z treścią) zaingestowana w całości: **fetch 1953/1953 (1938 pobrane,
14 już w magazynie z listy priorytetowej, 1 błąd — dokument 212 MB przekraczający limit
System.Text.Json, akceptowalny odstający przypadek), process total=2916 (T1+T2 razem po fast-skip),
inserted=1462, failed=0**.

Po drodze naprawiony drugi bug (poza 303-przekierowaniem z T1): `RawProcessRunner` wywalał się
z nieobsłużonym `TaskCanceledException` po 300 s timeoucie pojedynczego wywołania TEI — mimo że ma
już wbudowany mechanizm odporności (fail-streak, ODP-2/ODP-3), ten konkretny wyjątek się wymykał.
Rozwiązane przez proste wznowienie (idempotentne, fast-skip) — **kod NIE został zmieniony**,
świadomie, bo to dotyka rdzenia pipeline'u ingestii (ryzyko utraty danych ważniejsze niż wygoda);
do rozważenia osobno, jeśli się powtórzy.

Golden-set rozszerzony równolegle: 40 → 53 pozycje (10 kazusów/spraw z produkcji, `oki-limit-wplat`
— formalizacja incydentu retrievalowego z tej sesji, `prod-552kpk-cytat`, `prod-kradziez-200zl`).

## 1. Wynik zbiorczy

| metryka | T1 (40 poz.) | T2 (53 poz.) |
|---|---|---|
| Recall@K (retrieval) | 71% (20/28) | 68% (21/31) |
| Trafność abstynencji | 75% | 81% |
| Świeżość | 0% (1 poz., niepowiązane z UE) | 0% (bez zmian, oczekiwane) |
| Śr. similarity w korpusie / poza | 0,844 / 0,855 | 0,846 / 0,862 |

Recall@K spadło nominalnie (71%→68%), ale to mylące przy zmienionym mianowniku (28→31 pozycji o
różnej trudności). **Właściwe porównanie to te same 28 pozycji z T1, sprawdzone ponownie po T2** —
patrz sekcja 2.

## 2. Ten sam pool 28 pozycji z T1 — realny efekt T2: 20/28 → 18/28

| grupa | T1 | T2 | zmiana |
|---|---|---|---|
| Polskie (9, w tym `uodo-107` świadomie czerwona) | 6/9 | 6/9 | bez zmian |
| `ue-*` merytoryczne (18) | **13/18** | **11/18** | **−2** |
| `out-rodo` (1, InCorpus od T1) | 1/1 | 1/1 | bez zmian |

**Nowa regresja, dwie pozycje UE, które w T1 trafiały, teraz nie trafiają:**

| pozycja | T1 sim / hit | T2 sim / hit |
|---|---|---|
| `ue-rodo-6` | 0,7988 / **True** | 0,799 / **False** |
| `ue-zywnosc-9` | 0,8561 / **True** | 0,856 / **False** |

Similarity praktycznie identyczne (różnica w 3.–4. miejscu po przecinku — szum, nie realna zmiana
sygnału) — **to nie jest osłabienie samego dopasowania, to przesunięcie RANKU** wśród kandydatów.
Dokładnie mechanizm R7 opisany w `POMIAR-PRAWO-UE-PO.md` (T1): ~1462 nowych dokumentów UE z T2
dodało konkurentów do tej samej, stałej puli `CandidatesPerPath=50` — tym razem wypychając nie tylko
polskie przepisy (jak w T1), ale **przepisy UE wypychające inne przepisy UE**. Mechanizm generalizuje
się poza rywalizację PL-vs-UE.

`kpk-41` i `uodo-60` (regresja potwierdzona w T1 przez `--probe-chunk`, ranga #67/#88 przy oknie 50)
**pozostały bez zmian** — similarity identyczne co w T1, nadal `hit=False`. Nie pogłębiło się
mierzalnie (przynajmniej nie w similarity; `--probe-chunk` nie był ponownie odpalony na tych dwóch
pozycjach po T2 — dokładna ranga po dołożeniu T2 nieznana, tylko wynik binarny hit/miss).

## 3. Dobra wiadomość: formalizacja incydentu OKI działa

`oki-limit-wplat` (dodane w tej sesji, `expectedEli=DU/2026/1098`, `expectedArticle=26`, zweryfikowane
w tekście — zwolnienie 25 000/100 000 zł rocznie) — **`hit=True`**. Ten sam mechanizm diagnozowany na
początku tej rozmowy (zły artykuł tego samego aktu wygrywał różnicą cosine ~0,05) teraz jest
formalnie w golden-secie i przechodzi. `prod-552kpk-cytat` i `prod-kradziez-200zl` (nowe, zweryfikowane
pozycje) — również `hit=True`.

## 4. Wniosek dla decyzji o `CandidatesPerPath`

Rekomendacja z `POMIAR-PRAWO-UE-PO.md` (podnieść `CandidatesPerPath` i zmierzyć koszt PRZED T2,
albo świadomie zaakceptować ryzyko) — autor wybrał **świadomą akceptację ryzyka**. Ten pomiar
pokazuje realną cenę tej decyzji: **T2 kosztowało 2 dodatkowe trafienia UE, na dokładnie tym samym
mechanizmie co przewidziano.** To nie jest zaskoczenie ani nowy problem — to zmierzony koszt decyzji
podjętej świadomie. Jeśli planowana jest transza T3 albo dalsza rozbudowa korpusu UE, ten sam koszt
będzie się powtarzał proporcjonalnie do wolumenu — `CandidatesPerPath=50` jako stała, niezależna od
rozmiaru korpusu, zostaje jedynym niezaadresowanym czynnikiem ryzyka.

## 5. Bramka T3 (jeśli planowana)

Na podstawie tego pomiaru: UE strona nadal się rozwija poprawnie (11/18 to nadal ogromna poprawa
względem 0/18 sprzed T1), ale koszt dla istniejących trafień (PL i UE) będzie się kumulował z każdą
kolejną transzą, dopóki `CandidatesPerPath` zostaje na 50. Rekomendacja: przed T3 albo podnieść ten
parametr i zmierzyć koszt (jak proponowano przed T2), albo świadomie zaakceptować dalszy, przewidywalny
spadek na marginalnych pozycjach — decyzja taka sama jak przy T2, teraz z dwoma pomiarami w ręku
zamiast jednego.
