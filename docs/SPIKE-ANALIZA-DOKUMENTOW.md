# Spike: Analiza dokumentów (map-reduce) — SPK

Data: 2026-07-22 · Branch: `spike/analiza-dokumentow` (od `feat/halfvec-retriever`) · Status: **kod + testy gotowe (360/360), E2E na żywym stacku DO WYKONANIA** (checklist niżej).

## Cel spike'u

Sprawdzić, czy analiza dokumentu **fragment po fragmencie** (map-reduce) rozwiązuje problem, przez
który pliki wycięto z MVP: jeden prompt na cały plik dawał jedną zbiorczą odpowiedź zamiast analizy
punkt-po-punkcie. Kluczowa liczba do zmierzenia w E2E: **czas całości i per jednostkę na Bieliku/3060**
— jeśli >5 min dla umowy ~10 §, wniosek brzmi „wymaga mocniejszego backendu" i to też jest wynik.

## Przepływ

```
/analiza: upload PDF + prompt („oceń ryzyka dla najemcy")
   │  PdfAttachmentExtractor (REUŻYCIE DOC-0: limity 10 MB/100 str., bramka skanów)
   ▼
LegalUnitSplitter (SPK-1): jednostki LOGICZNE — § / art. / pkt / fallback akapitowy
   │  podgląd liczby jednostek PRZED startem (użytkownik widzi, ile zapytań LLM uruchomi)
   ▼
AnalysisSession w AnalysisSessionStore (SPK-2): in-memory, TTL 60 min, id = bilet powrotu
   ▼
AnalysisRunner (SPK-3), W TLE poza obwodem Blazora:
   1. embeddingi jednostek (TEI, raz — do routingu dopytań)
   2. MAP: per jednostka PEŁNY ChatService (retrieval korpusu + ugruntowanie + abstynencja +
      anty-fabrykacja ZA DARMO), semafor Analysis:MaxParallelism (domyślnie 2 — lokalny LLM
      i tak generuje sekwencyjnie), scope DI per jednostka (DbContext nie jest thread-safe);
      werdykt z pierwszej linii: OK / RYZYKO / BRAK ŹRÓDEŁ
   3. REDUCE: raport składany MECHANICZNIE (werdykty + cytaty [n] przenoszone strukturalnie,
      NIE przez LLM — anty-fabrykacja); LLM pisze tylko streszczenie z zakazem nowych twierdzeń
   ▼
UI: postęp k/n na żywo (event Changed) → karty per jednostka (RYZYKO/BŁĄD otwarte) → streszczenie
   ▼
Dopytania (SPK-6): routing kontekstu PER PYTANIE — odwołania wprost (§ 7) → cosine po embeddingach
jednostek → tryb przekrojowy (tabela werdyktów); kontekst jako tura-kotwica ChatTurn (budżet 1500 zn
GroundedPrompt, wybrane jednostki z przodu), retrieval korpusu działa dalej.
```

## Decyzje projektowe (z uzasadnieniem)

1. **Map przez pełny `ChatService`, nie goły LLM** — retrieval, bramka abstynencji i walidator
   cytatów działają per jednostka bez nowego kodu. Koszt: pełny retrieval per jednostka (świadomy
   skrót spike'a; przy produkcyjnej wersji można współdzielić część retrievalu).
2. **Reduce mechaniczny, LLM tylko streszczenie** — cytaty [n] z fazy map mają numerację PER
   JEDNOSTKA; przepuszczenie ich przez drugi LLM zerwałoby powiązanie ze źródłami i otworzyło
   fabrykację w syntezie. Odmowa per jednostka („BRAK ŹRÓDEŁ dla § 9") jest uczciwa i naturalna.
3. **Zero-persistence jak w DOC**: treść dokumentu żyje wyłącznie w pamięci procesu (tajemnica
   zawodowa). Id sesji = bilet do postępu i dopytań; TTL 60 min od ostatniego użycia; restart
   procesu = sesje znikają (komunikowane w UI).
4. **Równoległość ograniczona, nie fan-out** — na 3060 z jednym Bielikiem równoległe requesty nie
   przyspieszają generacji (i mogą wysycić VRAM); `MaxParallelism=2` default, podnosić dla API cloud.
   Realny zysk UX = postęp i wyniki cząstkowe, nie równoległość.
5. **Kotwica dopytań przeżywa `GroundedPrompt.TakeLast(4)`** — historia = kotwica + maks. 3 ostatnie
   dopytania; kotwica komponowana OD NOWA per pytanie (świeży routing).
6. **Twarde limity**: `MaxUnits=40` per dokument (nadmiar ucinany z JAWNĄ flagą w UI, nigdy po
   cichu); CostGuard liczy każdą jednostkę i streszczenie jako osobne zapytania dzienne.

## Klocki i testy (360/360 zielone, w tym 38 nowych)

| Task | Kod | Testy |
|---|---|---|
| SPK-1 | `LegalUnitSplitter` (Api/Services) | `LegalUnitSplitterTests` — 9: § line-start, tekst płaski PdfPig z filtrem odwołań (numeracja kolejna + przyimki), pkt, fallback, śmieci, cięcie oversize |
| SPK-2 | `AnalysisSession`, `AnalysisSessionStore`, `AnalysisOptions` | `AnalysisSessionStoreTests` — 6: cykl statusów, licznik przy współbieżnym zapisie, TTL na fake-zegarze |
| SPK-3 | `AnalysisRunner`, `AnalysisPrompts` | `AnalysisRunnerTests` — 12: happy path, semafor, abstynencja=BRAK ŹRÓDEŁ bez LLM, awaria jednostki nie wali sesji, CostGuard tnie w połowie, parsowanie werdyktu (fraza odmowy wygrywa) |
| SPK-6 | `AnalysisFollowUp` | `AnalysisFollowUpTests` — 11: routing § / paragraf / art. (rodzaj musi się zgadzać, części „(cz. n)" wchodzą), cosine w kolejności dokumentu, kompozycja kotwicy (budżet 1500, wybrane z przodu, tabela przekrojowa) |
| SPK-4 | `Analiza.razor`, DI w `Program.cs`, flaga `Analysis` w appsettings, link w nav, style | — (UI; weryfikacja w E2E) |

Przy okazji: baza na 3060 miała 2 zaległe migracje (`AddDemoConversations`, `AddChunkArticleNo` —
obie addytywne, backfill z lokatora) → zaaplikowane `dotnet ef database update`; wcześniej 24 testy
integracyjne padały na `column "ArticleNo" does not exist`.

## Jak włączyć

- Flaga `Analysis:Enabled` — **true w `appsettings.Development.json`** (spike), false w prod.
- Wymagania jak dla czatu: Postgres+pgvector (korpus), TEI (mmlw), LLM (`Llm:Provider=local` → Ollama/llama.cpp z Bielikiem).
- Wejście: nawigacja „Analiza dokumentów" albo `/analiza`.

## Checklist E2E (do wykonania na żywym stacku — TEI + Bielik)

1. **Umowa born-digital ~10 §** (np. umowa najmu wg `TEST-ZALACZNIK-UMOWA-NAJMU.md`) + prompt
   „oceń ryzyka dla najemcy" → podgląd pokazuje sensowne jednostki (§ 1…§ n, bez podpisów);
   **ZMIERZ: czas całości i per jednostkę** (kluczowa liczba spike'u).
2. Werdykty zróżnicowane (nie wszystko OK); cytaty [n] w kartach klikalne, prowadzą do źródeł
   TEJ jednostki; jednostki spoza korpusu → uczciwe BRAK ŹRÓDEŁ.
3. Streszczenie nie zawiera twierdzeń nieobecnych w kartach (wyrywkowo porównać).
4. F5 w trakcie analizy → analiza działa dalej (id sesji); po TTL/restarcie → czytelny komunikat
   o wygaśnięciu przy dopytaniu.
5. Dopytania: (a) „a co z § 7?" → kontekst z § 7 (odpowiedź odnosi się do treści tego paragrafu);
   (b) opisowe bez numeru → routing cosine trafia właściwą jednostkę; (c) „które paragrafy są
   najbardziej ryzykowne?" → odpowiedź z tabeli werdyktów.
6. Skan-PDF → odmowa bez wywołań LLM; plik >10 MB → komunikat.
7. Golden set `--chat` bez zmian (spike nie dotyka ChatService/GroundedPrompt) — wystarczy brak
   zmian w tych plikach w diffie (potwierdzone: spike tylko DODAJE pliki + rejestracje).

## Znane ograniczenia (świadome, spike)

- **Splitter na tekście płaskim** (PdfPig bez łamań linii) polega na heurystyce „numeracja kolejna +
  przyimki odwołań" — nietypowe numeracje (np. § 5a między § 5 i § 6) mogą wypaść do fallbacku
  akapitowego. Do oceny na realnych umowach w E2E.
- **Dopytania nie modyfikują raportu** („przeanalizuj § 7 głębiej" z aktualizacją werdyktu — osobna,
  cięższa funkcja).
- Brak endpointu REST `GET /api/analysis/{id}` (UI Blazor wystarcza; dołożyć jeśli spike przejdzie).
- Brak anulowania analizy z UI; brak OCR; jeden dokument na sesję; wyniki dopytań nie są
  persystowane (spójnie z zero-persistence).
- Werdykt zależy od dyscypliny Bielika w pierwszej linii — nieprzestrzeganie formatu degraduje się
  do werdyktu „?" (odpowiedź nadal widoczna); fraza odmowy ma pierwszeństwo.
