# Plan: Tryb kazusu (KAZ) — analiza stanu faktycznego zamiast pojedynczego pytania

## Cel i granice

Prawnik wkleja **opis stanu faktycznego** (kazus), system zwraca **ustrukturyzowane memo**:
lista zagadnień prawnych, a pod każdym ugruntowana odpowiedź z cytowaniami `[n]` i źródłami —
albo uczciwa odmowa, gdy korpus nie pokrywa zagadnienia.

**To NIE jest agent.** Decyzja z analizy kierunków rozwoju: Bielik 11B nie prowadzi wiarygodnie
otwartych pętli narzędziowych. Tryb kazusu to **deterministyczny workflow** — orkiestracja
w C#, LLM wołany tylko w dwóch dobrze zdefiniowanych rolach (ekstrakcja zagadnień, odpowiedź
per zagadnienie), obie z twardym parsowaniem/walidacją wyników.

## Przepływ (v1)

```
opis kazusu
   │
   ▼  LLM #1: ekstrakcja zagadnień (format liniowy, parsowany twardo)
zagadnienia (2–5, każde jako pytanie prawne)
   │
   ▼  per zagadnienie — SEKWENCYJNIE (wspólny scoped DbContext, jak follow-upy w ChatService)
retrieval (hybryda + QU + augmenter świeżości)  →  bramka abstynencji per zagadnienie
   │                                                   │ (za słaby sygnał → sekcja-odmowa,
   ▼                                                   ▼  LLM NIE jest wołany)
LLM #2..N: GroundedPrompt(zagadnienie, chunki) → odpowiedź sekcji + CitationValidator
   │
   ▼
memo = sekcje złożone w C# (BEZ syntezy LLM między sekcjami) + dyskleimer
```

## Decyzje projektowe (z uzasadnieniem)

1. **Ekstrakcja zagadnień w formacie liniowym, nie JSON.** Każda linia `ZAGADNIENIE: <pytanie>`;
   parser regexowy z fallbackiem na linie numerowane. JSON z Bielika to ruletka składniowa;
   linie parsują się odpornie, a nieudana ekstrakcja (0 linii) degraduje bezpiecznie: cały opis
   kazusu staje się jednym zagadnieniem (zachowanie ≈ zwykły czat, zero regresji wartości).
2. **Bez syntezy LLM między sekcjami.** Składanie memo w C# jest deterministyczne; przepuszczenie
   sekcji przez kolejny LLM otworzyłoby drzwi fabrykacji poza kontrolą CitationValidatora
   (cytaty `[n]` sekcji straciłyby kotwice). Anty-fabrykacja per sekcja = ta sama gwarancja
   co w czacie.
3. **Abstynencja per zagadnienie, nie per kazus.** Kazus może mieć 3 zagadnienia pokryte
   korpusem i 1 poza nim (np. prawo UE) — memo mówi to wprost per sekcja. To jest feature
   („mapa czego nie wiemy"), nie kompromis.
4. **Limit zagadnień: 5** (+ obcięcie w parserze). Chroni budżet (≤6 wywołań LLM per analiza)
   i czytelność memo.
5. **Zagadnienie jako pytanie prawne** („Czy najemcy przysługuje…?") — bo pytanie jest
   jednocześnie najlepszym zapytaniem do retrievalu (tor gęsty) i nagłówkiem sekcji memo.
6. **Sekwencyjnie, nie równolegle** — scoped DbContext nie jest thread-safe (ta sama decyzja
   co przy podwójnym retrievalu follow-upów). Latencja ~5×czat jest komunikowana w UI
   (sekcje pojawiają się progresywnie, streaming per sekcja).
7. **Limity:** analiza kazusu = **1 slot RateGuard + 1 slot CostGuard** (TryAcquire raz,
   Record sumą znaków wszystkich sekcji). Świadomie: kazus kosztuje ~5× pytanie czatu,
   a zjada 1 z 50 dziennych requestów testera — na pilotaż akceptowalne, monitorować
   licznikiem tokenów (Diagnostics już to umie). Action item E6: osobny mnożnik w CostGuard.
8. **v1 bez zapisu do historii rozmów.** ConversationStore modeluje pary pytanie-odpowiedź;
   memo to inna struktura. Zapis = osobna decyzja produktowa po feedbacku testerów
   (action item; do tego czasu prawnik ma „Kopiuj memo").
9. **v1 bez parytetu SSE (/api/kazus).** Jedynym konsumentem jest UI Blazor; endpoint SSE
   dodamy, gdy pojawi się konsument zewnętrzny (parytet czatu powstał, bo istniały runbooki
   curl). Action item w backlogu.
10. **Osobna strona `/kazus`, nie przełącznik w czacie.** Inny model interakcji (formularz →
    raport vs konwersacja), inna obietnica UX; przełącznik w czacie sugerowałby follow-upy
    po memo, których v1 nie wspiera.
11. **Model per ROLA (ekstrakcja / sekcje osobno), zwycięzcy z POMIARÓW, domyślnie Bielik.**
    Docelowy hosting: **CloudFerro Sherlock AI** (polska chmura; Bielik i PLLuM 12B dostępne,
    Gemma ~31B zapowiedziana; wszystkie €0,56/1M tokenów in/out) — równa cena czyni wybór
    modelu decyzją czysto jakościową, czyli MIERZALNĄ runnerem egzaminacyjnym:
    - tryb `solo` per model (działa BEZ korpusu — można w trakcie embeddingu): bazowa wiedza
      prawna PL każdego modelu;
    - po korpusie tryb `rag` per model + golden-set `--chat` per model: wierność źródłom,
      odsetek czystych cytowań `[n]`, zachowanie frazy odmowy — prompty groundingu były
      strojone pod Bielika i KAŻDY kandydat do roli sekcji musi te testy przejść.
    Role różnią się wymaganiami (ekstrakcja = dyscyplina formatu; sekcje = wierność
    źródłom + polska proza + posłuszeństwo cytowaniom), więc konfiguracja
    `Llm:Roles:<rola>` wskazuje model per rola; „cały kazus przez jeden model" to
    dopuszczalny WYNIK pomiarów, nie założenie.
    Zgodność z decyzją „zero amerykańskich API LLM": dotyczy ona PRZEPŁYWU DANYCH, nie
    narodowości wag — Sherlock (PL/UE) spełnia ją dla wszystkich trzech modeli. Przed
    pilotażem sprawdzić: DPA/klauzula nietrenowania na danych, rate limity, latencję.
12. **Podsumowanie wykonawcze — TAK; rewrite sekcji przez LLM — NIGDY.** Parafraza sekcji
    zrywa kotwice cytowań `[n]` (CitationValidator traci punkt odniesienia — anty-fabrykacja
    umiera dokładnie tam, gdzie zaufanie jest najcenniejsze). Bezpieczna forma „ładnego
    feedbacku": LLM pisze 3–5 zdań syntezy NAD nietkniętymi sekcjami, z programową walidacją
    (podsumowanie nie może wprowadzać `[n]` ani `art. X` nieobecnych w sekcjach; naruszenie →
    memo renderuje się bez podsumowania). Sekcje pozostają jedynym nośnikiem twierdzeń.

## Taski implementacyjne

### KAZ-0: `KazusPrompt` — ekstrakcja zagadnień + parser (PrawoRAG.Llm)
- `src/PrawoRAG.Llm/Grounding/KazusPrompt.cs`:
  - `BuildIssueExtraction(string kazus)` → `LlmRequest` (system: rola prawnika-analityka,
    format `ZAGADNIENIE: <pytanie>`, zakaz odpowiadania i tekstu poza liniami; Temperature 0,
    MaxTokens ~400);
  - `ParseIssues(string answer)` → `IReadOnlyList<string>`: linie `^\s*ZAGADNIENIE:\s*(.+)`,
    fallback `^\s*\d+[\.\)]\s*(.+)`; trim, deduplikacja, odrzucenie < 10 znaków, cap 5.
- Czysta klasa — testowalna bez LLM.

### KAZ-1: zdarzenia + serwis (PrawoRAG.Api/Services)
- `KazusEvents.cs`: `KazusEvent` (abstract) + `KazusIssuesEvent(Issues)`,
  `KazusIssueStartEvent(Index, Issue)`, `KazusSourcesEvent(Index, Sources)` (reużywa
  `ChatSource`), `KazusTokenEvent(Index, Delta)`,
  `KazusIssueDoneEvent(Index, Abstained, Check)`, `KazusDoneEvent(Issues, Abstained, Usage)`.
- `IKazusService` + `KazusService(IRetriever, ITemporalAugmenter, ILlmProvider,
  IOptions<RetrievalOptions>)` — konstrukcja lustrzana do `ChatService`:
  - LLM #1 → `ParseIssues`; 0 zagadnień → fallback: `[opis kazusu]` jako jedno zagadnienie;
  - pętla per zagadnienie: retrieval → `AbstentionPolicy.ShouldAbstain` → (odmowa sekcji |
    augmenter best-effort → `GroundedPrompt.Build(zagadnienie, chunks)` → stream →
    `CitationValidator`);
  - agregacja `LlmUsage` (suma in/out, `Estimated` = OR) — do `KazusDoneEvent`;
  - wyjątek per zagadnienie nie zabija analizy (sekcja-błąd, reszta leci) — wzorzec
    kwarantanny jak w ingestii.
- Rejestracja `AddScoped<IKazusService, KazusService>()` w `Program.cs` (obok IChatService).

### KAZ-2: strona `/kazus` (Blazor)
- `Components/Pages/Kazus.razor` (`@rendermode InteractiveServer`):
  - textarea (duża, na wielo-akapitowy stan faktyczny) + przycisk „Analizuj kazus";
  - guardy jak w czacie: `_userId` z `AuthenticationStateProvider` raz per circuit
    (NIE IHttpContextAccessor — znany bug tożsamości), `RateGuard.TryAcquire`,
    `CostGuard.TryAcquire`/`Record`, `_notice` na komunikaty limitów;
  - render progresywny: lista zagadnień → sekcje wypełniają się streamingiem; per sekcja
    badge cytatów (✓/⚠ jak w czacie), źródła w `<details class="sources">` (reużycie CSS),
    sekcja-odmowa jako `banner-warn`;
  - stopka: dyskleimer („wstępny research, nie porada prawna") + „Kopiuj memo"
    (markdown całości do schowka) + licznik tokenów za flagą Diagnostics;
  - link w `MainLayout` nav: „Analiza kazusu".
- `/o-systemie`: akapit o trybie kazusu (co robi, że sekcje mogą odmawiać, prośba o feedback).

### KAZ-3: testy (fakes, bez DB/LLM — wzorzec ChatServiceFollowUpTests)
1. `ParseIssues`: format ZAGADNIENIE, fallback numerowany, mieszany szum wokół linii,
   deduplikacja, cap 5, pusta odpowiedź → `[]`.
2. `KazusService`: szczęśliwa ścieżka 3 zagadnień → sekwencja zdarzeń (Issues → 3×(Start,
   Sources, Token…, IssueDone) → Done), każdy retrieval dostał TEKST zagadnienia,
   GroundedPrompt dostał zagadnienie (nie cały kazus).
3. Abstynencja: 1 z 3 zagadnień pod progiem → sekcja `Abstained`, LLM wołany 1+2 razy
   (ekstrakcja + 2 sekcje), `KazusDoneEvent.Abstained == 1`.
4. Fallback ekstrakcji: LLM zwraca prozę bez linii → jedno zagadnienie = opis kazusu.
5. Wyjątek retrievalu przy zagadnieniu #2 → sekcja-błąd, #3 przetworzone, Done z kompletem.
6. Usage: suma tokenów z ekstrakcji + sekcji; `Estimated` propagowane.
7. Regresja: pełny `dotnet test`.

### KAZ-4: weryfikacja E2E (M4, po zakończeniu embeddingu — checklist do runbooka)
1. Kazus „najem + zaległości + eksmisja" (pokryty korpusem) → memo z ≥2 sekcjami,
   cytaty ✓, źródła klikalne.
2. Kazus z wątkiem spoza korpusu (np. prawo pracy UE) → właściwa sekcja odmawia,
   pozostałe odpowiadają.
3. Limity: `Access__MaxUserRequestsPerDay=2` → trzecia analiza daje komunikat limitu.
4. Tokeny: flaga Diagnostics pokazuje sumę ~5× większą niż pytanie czatu (potwierdzenie
   decyzji #7).

### KAZ-7: bake-off modeli Sherlock (solo — od zaraz; rag/chat — po korpusie)
- Konfiguracja providera na endpoint Sherlock (OpenAI-compatible — bez zmian w kodzie,
  `LocalLlm__BaseUrl`+`ApiKey`+`Model`).
- Runda 1 (bez korpusu): `--exam Eval__ExamModes=solo` × {Bielik, PLLuM} (+ Gemma gdy wejdzie);
  tabela trafności + rozkład liter do docs/EVAL-LOG.md.
- Runda 2 (po korpusie, na M4 lub serwerze): `rag` per model + golden-set `--chat` per model
  (czyste cytowania, fraza odmowy).
- Wynik: przypisanie modeli do ról w konfiguracji kazusa (i ewentualnie rewizja modelu czatu).

### KAZ-5: model per rola (opt-in wg wyników KAZ-7)
- `LlmRoleOptions` (sekcja `Llm:Roles:<rola>`: BaseUrl, Model, ApiKey) + `ILlmRoleResolver`
  z metodą `GetForRole(string role)` — zwraca domyślny `ILlmProvider`, chyba że rola ma
  własną sekcję (wtedy dedykowany `OpenAiCompatibleLlmProvider` z własnym HttpClientem,
  cache per rola).
- `KazusService`: ekstrakcja przez `GetForRole("kazus-extraction")`, sekcje ZAWSZE przez
  domyślny provider (Bielik).
- Testy: brak sekcji → ten sam provider dla obu ról; sekcja obecna → ekstrakcja innym
  providerem, sekcje domyślnym.
- Przed włączeniem na M4/OVH: `--exam` trybem solo na Gemmie (benchmark wiedzy PL).

### KAZ-6: podsumowanie wykonawcze memo (v1.1 — po feedbacku z v1)
- Prompt: synteza 3–5 zdań NAD sekcjami (rola `kazus-summary`, może być Bielik);
  zakaz nowych cytowań i numerów artykułów.
- Walidator podsumowania (czysty, testowalny): zbiór `[n]` i wzorców `art. \d+\w*` z sekcji;
  podsumowanie wprowadzające nowe → odrzucone (memo bez podsumowania + log).
- UI: blok „Podsumowanie" nad sekcjami, wizualnie odróżniony (to nawigacja, nie źródło prawdy).

## Poza zakresem v1 (jawnie, jako backlog)
- Follow-upy po memo („pogłęb zagadnienie 2") — wymaga modelu konwersacji dla memo.
- Zapis analiz do historii (decyzja produktowa po feedbacku testerów).
- Parytet SSE `/api/kazus`.
- Równoległy retrieval zagadnień (wymaga scope per zagadnienie — jak ODP w ingestii).
- Pliki jako wejście (kierunek #1) i anonimizacja (kierunek #2) — osobne plany.
- Osobny mnożnik kosztu kazusu w CostGuard (E6.3).

## Kolejność
KAZ-0 → KAZ-1 → KAZ-3 (testy serwisu przed UI — UI to konsument) → KAZ-5 → KAZ-2 → KAZ-4 na M4.
KAZ-6 świadomie PO feedbacku z v1 (wymaga działającego memo, żeby ocenić wartość syntezy).
Commit per task.
