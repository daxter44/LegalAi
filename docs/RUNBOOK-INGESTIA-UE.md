# Runbook: ingestia prawa UE (EUR-Lex / CELLAR)

Kolejność komend dla transzy T1 z `PLAN-PRAWO-UE.md` § 5.6. Wszystko wznawialne: magazyn surowych
pomija akty już pobrane, a pipeline pomija dokumenty już zaindeksowane (po `content_hash`).
Przerwanie w połowie nic nie psuje — kolejny przebieg dochodzi od miejsca.

Stan wejściowy (2026-08-26): pomiar „przed" wykonany, `14/40` przy zerowym trafieniu prawa UE
(`POMIAR-PRAWO-UE-PRZED.md`). Bramka Fazy 0 spełniona, więc T1 może startować.

## 0. Zanim ruszysz — co ta transza zrobi

| krok | co robi | czy dotyka bazy |
|---|---|---|
| 1. `discover` | pyta SPARQL o zakres i klasę aktów, wypisuje skład | nie |
| 2. `fetch` | pobiera treść do `data/raw/EURLEX/` (pliki na dysku) | nie |
| 3. `report` | normalizuje surowe i wypisuje jakość — bez embeddingu | nie |
| 4. `process` | normalizuje + embeduje + zapisuje dokumenty i chunki | **TAK** |

Kroki 1–3 są bezpieczne i tanie. Krok 4 to jedyny, który zmienia korpus — i dopiero po nim
pomiar „przed" traci sens, więc nie odpalaj go, jeśli `POMIAR-PRAWO-UE-PRZED.md` nie jest wypełniony.

## 1. Skład zakresu (bez pobierania treści)

```
Ingestion__Source=EURLEX Ingestion__Mode=discover \
EurLex__Discover__Enabled=true EurLex__Discover__YearFrom=2016 \
dotnet run --project src/PrawoRAG.Ingestion --no-launch-profile
```

Czego się spodziewać (zmierzone na roczniku 2025: 182 akty): ~40% aktów w klasie
`amending-absorbed`, czyli „tylko metadane", i ~60% w `substantive`/`amending-open`, czyli
„treść + chunki". Dla całego zakresu 2016+ to około 1 900 aktów, z czego ~1 100–1 200 z treścią.

**Na co patrzeć:** linia „aktów ma NIEPEŁNE metadane" na końcu. Endpoint CELLAR-a zwraca pod
obciążeniem 502, a wtedy klasa aktu i wybór wersji są niepewne — konektor takich aktów **nie pobiera**.
Jeśli ta liczba jest duża, powtórz krok 1 po kilku minutach; nie ma sensu iść dalej z dziurą w metadanych.

## 2. Pobranie treści do magazynu surowych

```
Ingestion__Source=EURLEX Ingestion__Mode=fetch \
EurLex__Discover__Enabled=true EurLex__Discover__YearFrom=2016 \
dotnet run --project src/PrawoRAG.Ingestion --no-launch-profile
```

Na próbę (5 aktów, kilkanaście sekund): dołóż `Ingestion__MaxItems=5`.

Tempo: `EurLex:RequestDelayMs` = 250 ms na żądanie, więc ~1 200 aktów to około godziny — świadomie,
żeby nie dostać bana po IP w środku transzy. Podniesienie tego na własne ryzyko.

**Podsumowanie na końcu wypisuje cztery liczby:** pobrano, pominięto jako metadane-only, pominięto
z niepełnymi metadanymi, pominięto bez tekstu PL. Ostatnia grupa to głównie akty sprzed 2004 r.
(polski tekst tylko w PDF — Faza 6) i ~1 000 aktów bez polskiej wersji w ogóle. To nie błąd ingestii,
ale ta liczba należy do raportu pokrycia, nie do logu.

## 3. Raport jakości PRZED embeddingiem (czysty CPU, za darmo)

```
Ingestion__Source=EURLEX Ingestion__Mode=report Ingestion__MaxItems=100 \
dotnet run --project src/PrawoRAG.Ingestion --no-launch-profile
```

To jest bramka Fazy 3 i najtańszy moment, w którym można wyłapać zepsute parsowanie.
**Na co patrzeć:**

- **akt z zerem segmentów** — alarm, nie linia w logu; oznacza dokument, którego nie rozpoznał żaden
  z trzech torów (kotwice / legacy / PDF), czyli możliwą zmianę schematu CELLAR-a;
- rozkład `parsePath` — jeśli nagle wszystko wpada w `LegacyText`, coś się stało z kotwicami;
- wpisy „Tekst BAZOWY" — akt bez polskiej konsolidacji, treść może nie uwzględniać nowelizacji;
- próbka tekstu: czy da się ją zacytować prawnikowi bez zażenowania (znaczniki `▼M1`, przypisy
  i formuły podpisowe powinny być już usunięte).

## 4. Przetworzenie do bazy (jedyny krok, który zmienia korpus)

```
Ingestion__Source=EURLEX Ingestion__Mode=process \
dotnet run --project src/PrawoRAG.Ingestion --no-launch-profile
```

Wymaga bazy i TEI (embeddingi). Idempotentny: akt o niezmienionej treści jest pomijany, a zmiana
treści (np. nowa konsolidacja) powoduje transakcyjną podmianę chunków — bez osieroconych wektorów.

Po tym kroku **od razu** dwie rzeczy:

1. `src/PrawoRAG.Eval/golden-set.json` — pozycja `out-rodo` musi zmienić kategorię na `InCorpus`
   z `expectedEli: "32016R0679"`. Dziś oczekuje odmowy („ochrona danych osobowych — brak w korpusie"),
   więc po ingestii karałaby system za poprawną odpowiedź.
2. Pomiar „po": `dotnet run --project src/PrawoRAG.Eval` (bez flag) → wyniki do
   `POMIAR-PRAWO-UE-PO.md`, razem z porównaniem 18 pozycji polskich (regresja = STOP dla T2).

## 5. Warunek zabicia (z planu, § 1)

Jeśli po T1 pozycje `ue-*` nadal nie trafiają, przyczyną **nie jest pokrycie** i dokładanie kolejnych
aktów nic nie da. Wtedy: diagnoza retrievalu/promptu (ryzyka R5–R7 planu), nie transza T2.
Odwrotnie też: jeśli trafiają, ale model cytuje polski akt do pytania unijnego, to ryzyko R6 —
sprawdzamy CO cytuje, nie tylko czy odpowiedział.

## 6. Awarie, które już wystąpiły, i co z nimi robić

| objaw | przyczyna | reakcja |
|---|---|---|
| `SPARQL HTTP 502` w logu | endpoint CELLAR-a pod obciążeniem | powtórz krok; akty z niepełnymi metadanymi są pomijane, nie zgadywane |
| „strona nie wniosła nowych aktów" | endpoint zignorował `OFFSET` | to bezpiecznik pętli stronicowania; jeśli powtarzalne, zawęź rocznik |
| akt pominięty „bez tekstu PL" | brak polskiego XHTML-a (często akt sprzed 2004) | oczekiwane; ścieżka PDF to Faza 6 |
| odpowiedź krótsza niż 2000 B | komunikat CELLAR-a, nie akt | odsiewany progiem `EurLex:MinContentBytes` |
