# Pomiar „PRZED" — prawo UE w korpusie (UE-0)

Artefakt bramkowy Fazy 0 z `PLAN-PRAWO-UE.md` § 5.1. **Bez wypełnionej tabeli wyników Faza 2
(pobieranie) nie startuje** — to zabezpieczenie przed powtórzeniem wpadki z polskim FTS, gdzie
metryka wyniku powstała po wykonaniu pracy.

## 1. Co jest gotowe (2026-08-26)

Zestaw pomiarowy: **22 nowe pozycje w `src/PrawoRAG.Eval/golden-set.json`** z prefiksem `ue-`
(razem plik ma 40 pozycji). Każdy przepis docelowy został **zweryfikowany w polskim tekście
z CELLAR-a** — nie wpisany z pamięci:

| pozycja | akt (CELEX) | art. | co weryfikowano w tekście |
|---|---|---|---|
| `ue-rodo-6` | 32016R0679 | 6 | ust. 1 lit. f) — prawnie uzasadniony interes |
| `ue-rodo-33` | 32016R0679 | 33 | tytuł artykułu + termin 72 h |
| `ue-rodo-17` | 32016R0679 | 17 | „prawo do bycia zapomnianym" |
| `ue-aiact-5` | 32024R1689 | 5 | „Zakazane praktyki w zakresie AI" |
| `ue-aiact-50` | 32024R1689 | 50 | obowiązki przejrzystości |
| `ue-aiact-deepfake` | 32024R1689 | 5 | **lit. ba) istnieje TYLKO w konsolidacji** `02024R1689-20260727` |
| `ue-dsa-16` | 32022R2065 | 16 | „Mechanizmy zgłaszania i działania" |
| `ue-dma-5` | 32022R1925 | 5 | „Obowiązki strażników dostępu" |
| `ue-konsument-9` | 32011L0083 | 9 | prawo odstąpienia (14 dni) |
| `ue-kierowcy-6` | 32006R0561 | 6 | dosłownie: „nie może przekroczyć 9 godzin" |
| `ue-zywnosc-9` | 32011R1169 | 9 | wykaz danych obowiązkowych |
| `ue-mdr-10` | 32017R0745 | 10 | ogólne obowiązki producentów |
| `ue-produkty-5` | 32023R0988 | 5 | ogólne wymaganie bezpieczeństwa |
| `ue-mar-17` | 32014R0596 | 17 | podawanie informacji poufnych |
| `ue-turystyka-12` | 32015L2302 | 12 | rozwiązanie umowy o imprezę |
| `ue-dsm-17` | 32019L0790 | 17 | odpowiedzialność platform |
| `ue-reach-33` | 32006R1907 | 33 | **weryfikowane w konsolidacji** (tekst bazowy: 404 dla PL) |
| `ue-eprivacy-5` | 32002L0058 | 5 | **tylko konsolidacja, markup legacy bez kotwic** |
| `ue-trap-95-46` | — | — | pułapka: dyrektywa uchylona przez RODO |
| `ue-trap-rodo-999` | — | — | pułapka: RODO ma 99 artykułów |
| `ue-out-ccpa` | — | — | poza korpusem (prawo Kalifornii) |
| `ue-related-ukgdpr` | — | — | pokrewne, ale poza korpusem (UK GDPR) |

Trzy z tych pozycji są **strażnikami mechanizmu**, nie tylko treści: `ue-aiact-deepfake` przechodzi
tylko wtedy, gdy ingestujemy tekst skonsolidowany, `ue-reach-33` — gdy konsolidacja jest próbowana
przed tekstem bazowym, `ue-eprivacy-5` — gdy istnieje tor parsowania dokumentów bez kotwic (UE-3.0).

Poprawiona też jedna pozycja zastana: `out-rodo` („zasady przetwarzania zgodnie z RODO",
`OutOfCorpus`) jest poprawna wyłącznie do transzy T1 — po zaingestowaniu RODO musi zmienić kategorię
na `InCorpus`, inaczej scorowanie zacznie karać system za poprawną odpowiedź. Zapisane w nocie pozycji.

## 2. Co trzeba uruchomić (operator, maszyna z korpusem)

Środowisko agenta nie ma dostępu do korpusu ani modeli (lokalnie brak bazy i TEI), więc oba przebiegi
odpala operator na maszynie z pełnym korpusem:

```
# 1. Recall + zachowanie bramki na zestawie (w tym 22 pozycje UE)
dotnet run --project src/PrawoRAG.Eval -- --exam

# 2. Odmowy na realnym ruchu (metryka nadrzędna fazy jakości)
dotnet run --project src/PrawoRAG.Eval -- --refusals
```

## 3. Wyniki „PRZED" — do wypełnienia

Oczekiwanie: **wszystkie pozycje `ue-*` z kategorii `InCorpus` kończą się odmową albo trafieniem
w niewłaściwy akt** (prawa UE nie ma dziś w korpusie). Jeśli któraś przechodzi z sensowną
odpowiedzią, to sygnał, że model odpowiada z pamięci parametrycznej mimo bramki anty-fabrykacji —
osobny problem do zapisania, ważniejszy od samej ingestii.

| pozycja | wynik (odmowa / trafienie / zła podstawa) | co trafiło do źródeł |
|---|---|---|
| … | | |

**Baseline polski (do porównań regresji po każdej transzy):** wynik `--exam` dla pozycji spoza
prefiksu `ue-` oraz `--refusals` (odsetek odmów). Liczby wpisać tutaj:

- pozycje PL: … / 18 trafionych
- odmowy na `refusal-set`: … %

## 4. Bramka

Faza 2 startuje, gdy w § 3 są liczby dla wszystkich 22 pozycji UE i baseline polski. Interpretacja:
- wszystkie UE = odmowa → ingestia ma sens, mierzymy poprawę po T1;
- część UE trafia → sprawdzić, czym trafia (możliwa konfabulacja albo polski odpowiednik przepisu);
- pozycje PL słabsze niż w ostatnim pomiarze → najpierw diagnoza regresji, potem prawo UE.
