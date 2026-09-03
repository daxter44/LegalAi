# Ewaluacja analizy dokumentów — BASELINE (AJ-1) — 2026-09-02

Pierwszy bieg harnessu `--analysis` (AJ-1b) na golden secie `analysis-set.json` (AJ-0) PRZED
jakąkolwiek zmianą pipeline'u fazy 1. Wszystkie kolejne biegi (AJ-7, AJ-10) porównują się z tym plikiem.

Stack: baza + TEI + reranker na `192.168.100.11` (stan korpusu z 2026-09-02), retrieval jak
produkcja (`GapClosingRetrieval`, augmenter, `OrderForGrounding`, TopK 8, próg odmowy 0.0).
Prompt jednostki = dokładnie `AnalysisPrompts.MapQuestion` produkcji.

## Tryb tylko-retrieval (`--no-generate`) — ZMIERZONE

Klucz LLM (Gemma przez OpenRouter, `OPENROUTER_API_KEY`) nie był dostępny w tej sesji, więc
metryki zależne od generacji (recall, fałszywe RYZYKO, BRAK ŹRÓDEŁ) **czekają na bieg z generacją**
(sekcja niżej). Metryka retrievalu jest niezależna od LLM i została zmierzona w całości.

| metryka | wynik |
|---|---|
| dokumentów / jednostek | 5 / 53 |
| **trafienie normy w źródłach** (oczekiwany akt + artykuł w top-8 jednostki) | **3 / 17 (18%)** |
| czas retrievalu, mediana | 5 s / jednostka |
| czas łączny (sam retrieval) | 4,7 min |
| druga runda retrievalu (gap-closing) | 0 / 53 |
| timeouty BM25 (`CommandTimeout=3 s`, zapytanie po akronimie) | 34 zdarzenia w logu, best-effort, nie wywracają jednostki |

Surowe wyniki: `logs/analysis-20260902-142114.jsonl`, podsumowanie `logs/analysis-summary-20260902-142114.json`.

### Trafienia normy per dokument

| dokument | normy w kluczu | trafione | co wróciło zamiast normy |
|---|---|---|---|
| najem-mieszkanie | 5 (uopl art. 6, 8a; KC 664, 483, 6) | **0** | wyłącznie orzeczenia sądów rejonowych o najmie; ustawa o ochronie praw lokatorów ani razu w top-8 |
| kurs-konsument | 3 (upk 27, KC 483, upk 7a) | **0** | orzeczenia SOKiK (XVII AmC) o klauzulach; upk pojawia się, ale art. 40 / 54 (rozdział timeshare) |
| regulamin-sklep | 3 (upk 27, 34, 32) | **0** | upk art. 53, 54, 44–50, 3 (timeshare / definicje); dyrektywa 2008/122/WE (timeshare) |
| dzielo-b2b | 3 (KC 483, 558, 509) | **0** | orzeczenia; brak KC |
| postanowienie-zaswiadczenie | 3 (KPA 219, 218, 219) | **3** | dokument SAM cytuje art. 217–219 KPA po numerze — retrieval trafia dokładnie |

### Wniosek diagnostyczny (potwierdza lukę B z przeglądu)

Retrieval napędzany brzmieniem klauzuli nie znajduje normy bezwzględnie obowiązującej, chyba że
dokument cytuje ją po numerze. Dla umów wraca niemal wyłącznie orzecznictwo; dla dokumentów
konsumenckich właściwa ustawa, ale niewłaściwy rozdział (słowa „odstąpienie", „umowa zawarta na
odległość" ciągną przepisy o timeshare). Bez normy w źródłach werdykt OK jest fałszywie uspokajający.

**Kontrpróba (sonda `--probe-akty`, ta sama baza):** pytanie sformułowane prawniczo
„Czy kaucja przy najmie lokalu mieszkalnego może wynosić 25-krotność miesięcznego czynszu?" daje
ustawę o ochronie praw lokatorów na **pozycji 1 i 2** w pełnym dense top-50 (45 aktów / 5 orzeczeń),
a art. 6 uopl na pozycji 7 w puli samych aktów. Ten sam korpus, ten sam embedder. Różnica tkwi w
sformułowaniu zapytania, nie w pokryciu korpusu. To bezpośrednio uzasadnia:
- AJ-3/AJ-4 (profil dokumentu jako kotwica dziedzinowa w zapytaniu),
- AJ-8/AJ-9 (zagadnienie prawne jako zapytanie retrievalu zamiast surowego tekstu klauzuli).

Drobne: lokalizator chunku „art. 44-50" (chunk obejmujący kilka artykułów) nie dopasuje się do
oczekiwanego „34" nawet gdyby to był właściwy fragment — metryka trafienia normy jest przez to
konserwatywna (zaniża, nie zawyża).

## Dopisek 2026-09-03: kotwica w prompcie nie działa, bo zapytaniem był cały prompt

Bieg `--no-generate --oracle-profile` PO AJ-4 (profil-wyrocznia z klucza doklejony do promptu
fazy map, zapytanie retrievalu = cały prompt): **trafienie normy 3/17 → 3/17, bez zmian.**
Diagnoza: `ChatService` embeduje jako zapytanie całą treść pytania, czyli intencję użytkownika +
kontekst + fragment + instrukcję formatu werdyktu („Pierwsza linia odpowiedzi to DOKŁADNIE…"),
a TEI ucina do 512 tokenów (`Truncate=true`). Kotwica tonie w instrukcji; słowa „WERDYKT",
„odpowiedzi", „uzasadnienia" trafiają też do BM25. Stąd AJ-4b: `retrievalQuery` rozdzielone od
promptu (kotwica + treść fragmentu, ≤1800 zn).

**Do wykonania po odzyskaniu dostępu do stacku (dwa biegi, kolejność ważna):**
1. `--analysis --no-generate --no-profile` — sama treść fragmentu jako zapytanie (nowy baseline
   retrievalu po AJ-4b; porównać z 3/17).
2. `--analysis --no-generate --oracle-profile` — treść + kotwica; różnica względem (1) = efekt
   kotwicy w czystej postaci. Jeśli ≈0, kotwica dziedzinowa nie wystarcza i AJ-8 (zagadnienie
   prawne jako zapytanie) jest konieczne; sonda z 2026-09-02 (pytanie prawnicze → uopl na pozycji 1)
   sugeruje, że tak będzie.
Biegi z 2026-09-03 11:0x–11:4x przerwane: stack niedostępny (praca zdalna), 190 s timeoutów per jednostka.

## Tryb z generacją — DO WYKONANIA

Bieg: `Llm__Provider=local Llm__Local__BaseUrl=... Llm__Local__Model=... Llm__Local__ApiKey=$OPENROUTER_API_KEY`
+ te same override'y bazy/TEI/rerankera, `dotnet run --project src/PrawoRAG.Eval -- --analysis`.
Uzupełnić tabelę: recall wbudowanych ryzyk (0/14 oczekiwanych RYZYKO poza NeedsLawyer), fałszywe
RYZYKO na 27 jednostkach bez wady, BRAK ŹRÓDEŁ na jednostkach z treścią prawną, liczba „?" i
`finish_reason=length` (AJ-2), czas per jednostka z generacją.

Oczekiwanie (hipoteza do sfalsyfikowania): przy 18% trafienia normy recall wbudowanych ryzyk będzie
niski, a część RYZYKO, które model jednak wystawi, będzie ugruntowana w orzeczeniach, nie w przepisie.
