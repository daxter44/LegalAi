# Plan implementacji: widoczność pracy, router intencji, bramka anty-fabrykacji, pętla domykająca lukę, retrieval jako narzędzie

**Data:** 2026-08-24 (v4 — router wydzielony jako osobna, wczesna faza, odłączony od tool callingu).
**Branch bazowy:** `feat/halfvec-retriever` @ `853a670`.
**Model odpowiadający:** Gemma 4 31B IT.
**Model pomocniczy (router + przeformułowanie zapytań):** lekki, 6–11 B — domyślnie Bielik 11B
(jest już w `LocalLlmOptions`), wybór finalny evalem, nie preferencją.

Plan jest ciągły: weryfikacja i dopieszczanie po zintegrowaniu całości, nie po każdej fazie.
Testy jednostkowe/integracyjne są częścią każdej fazy (dowód braku regresji). Testy manualne
i metody pomiaru — na końcu, dla całości.

## Problemy, które ten plan rozwiązuje (z zamówienia)

1. **Użytkownik nie widzi, że system pracuje** — 85 s ciszy (pomiar `PRAWORAG_LOG_TIMING`:
   ~35 s reranker, ~45 s LLM, z czego ~41 s rozumowanie). → Faza 1.
2. **Każde pytanie przechodzi pełny RAG** — „siema" kosztuje 85 s i kończy się odmową;
   nie ma żadnego routera (`ChatService.AskAsync` startuje od `FollowUpSelector.SelectAsync`
   bez warunku). → Faza 2 (router) + Faza 5 (tool calling).
3. **System odmawia, gdy mógłby odpowiedzieć** — odmowy strukturalne (terminologia,
   luka jednej rundy retrievalu). → Faza 4 (pętla domykająca) + Faza 6 (pomiar na żywym ruchu).
4. **Twarde ograniczenie bezpieczeństwa:** model NIGDY nie odpowiada na pytanie prawne
   z pamięci parametrycznej, bez weryfikacji w źródłach. → decyzje przekrojowe + Faza 3
   (bramka anty-fabrykacji) + bezpiecznik w Fazie 2.

**Poza zakresem, świadomie:** przyspieszanie rerankera (osobne zadanie; warunek wstępny trybu
w pełni agentowego, nie tego planu), ingestia prawa UE (właściwy kształt: wąska lista aktów po
CELEX — RODO `32016R0679`, AI Act `32024R1689`, DSA, DMA — jako zależność jakościowa przyszłego
trybu redakcyjnego), tryb redakcyjny (pisanie regulaminów/umów — wymaga prawa UE w korpusie
i osobnego kontraktu gruntowania).

## Role modeli — żeby nie było wieloznaczności

| model | rola | kiedy pracuje |
|---|---|---|
| **Bielik 11B (pomocniczy)** | (a) **router**: PRZED retrievalem orzeka „to na pewno nie jest pytanie prawne" albo „idź do bazy z takim zapytaniem"; (b) **przeformułowanie**: gdy retrieval nie domknął pytania, przekłada pytanie na terminologię ustawową dla DRUGIEJ rundy retrievalu | (a) na początku każdej tury; (b) tylko gdy nie powstała odpowiedź merytoryczna |
| **Gemma 4 31B (główny)** | pisze odpowiedź na dostarczonych źródłach; w Fazie 5 dodatkowo formułuje wywołania narzędzia `szukaj_w_przepisach` | po retrievalu (albo od razu, dla small-talku) |

Model pomocniczy **nigdy nie rozmawia z użytkownikiem i nigdy nie pisze odpowiedzi** — jego
wyjścia to decyzja routingu i zapytania do bazy. Nie „dopytuje" użytkownika.

## Decyzje przekrojowe

1. **Jedno miejsce prawdy dla ścieżki retrievalu.** Wszystko, co dotyka wyboru źródeł, ląduje
   w `PrawoRAG.Domain` i jest wołane z JEDNEGO wejścia przez `ChatService`, endpoint SSE
   `/api/chat` i `PrawoRAG.Eval` (blizna: commit `1de510b` — „rozjazd kopii = rozjazd metryki").
2. **Każda zmiana zachowania za flagą konfiguracyjną** (wzorem `Documents:Enabled`),
   domyślnie dającą dzisiejsze zachowanie tam, gdzie zmienia treść odpowiedzi.
3. **Fail-safe zawsze w stronę retrievalu.** Awaria/timeout/nieparsowalne wyjście modelu
   pomocniczego ⇒ retrieval (dzisiejsza ścieżka). Nigdy „pomiń bazę", nigdy poluzowana bramka.
4. **Asymetria pomyłki routera jest wpisana w konstrukcję.** Small-talk wpuszczony do
   retrievalu = 85 s straty. Pytanie prawne uznane za small-talk = odpowiedź bez źródeł =
   złamany rdzeń produktu. Dlatego router ma prawo orzec tylko jedno: „tu NA PEWNO nie ma nic
   prawnego". Wszystko inne, w tym każda wątpliwość, idzie do bazy.
5. **Zero nowych list słów kluczowych.** Rozpoznawanie odwołań prawnych — wyłącznie istniejące
   parsery (`CitationParser`, `ActAliases`, `CaseNumberKey`, `AcronymDetector`).

---

# Faza 1 — Widoczność pracy

**Co dowozi:** pierwszy sygnał w UI <2 s od wysłania; rozumowanie modelu widoczne w trakcie
generacji, nie po niej. Zero zmian w treści odpowiedzi.

**Punkt wyjścia:** rozumowanie już przychodzi per token (`OpenAiCompatibleLlmProvider.cs:119-129`,
flaga `isThought`), ale jest buforowane przez `ReasoningSplitter` i oddawane raz, po zakończeniu
strumienia (`ChatService.cs:107`). W UI przez ~85 s widać tylko `…` na przycisku (`Chat.razor:222`).

### 1.1 Kanał zdarzeń z callbacków
`ChatService.AskAsync` to iterator asynchroniczny — **z callbacku nie da się `yield return`**,
a etapy retrievalu i delty rozumowania powstają wewnątrz `await`owanych wywołań. Wprowadzić
`System.Threading.Channels.Channel<ChatEvent>`: callbacki piszą, główna pętla iteratora drenuje
i wypuszcza. Kanał domknięty i wydrenowany przed `DoneEvent`.

*(Alternatywa — zmiana kontraktu `ILlmProvider.StreamCompletionAsync` na strumień tagowanych
delt — czystsza długoterminowo, ale rusza `ClaudeLlmProvider`, `AnalysisRunner` i eval.
Kanał wybrany jako mniej inwazyjny.)*

### 1.2 Rozumowanie na żywo
- `LlmRequest` (`ILlmProvider.cs`): `Action<string>? OnReasoningDelta` — obok `OnUsage`/`OnReasoning`.
- `OpenAiCompatibleLlmProvider`: wywołanie dla każdej delty z `isThought`; `ReasoningSplitter`
  nadal buforuje całość dla `OnReasoning` (zapis do historii bez zmian).
- `ClaudeLlmProvider`: bez zmian — pole opcjonalne.

### 1.3 Etapy retrievalu
- `RetrievalStage(string Name, string Label, int? Count)` w `PrawoRAG.Domain/Retrieval`.
- `RetrievalQuery` (`Retrieval.cs`): `IProgress<RetrievalStage>? Progress` (wzorzec
  `RerankText`/`ExactMatchText`).
- `HybridRetriever`: raport etapu **w tych samych punktach, gdzie już stoi `LatencyLog.Mark`**
  (`embed`, `dense`, `sparse`, `acronym`, `fetch_candidates`, `rerank.main`, tory, `citation_bridge`,
  `rerank.bridge`) — jedno źródło dla instrumentacji i UI, nie mogą się rozjechać.
- `FollowUpSelector`: `Progress` do OBU przebiegów, oznaczonych (`surowy`/`kontekstowy`) —
  follow-up to dwa pełne retrievale i użytkownik musi widzieć, czemu trwa dwa razy dłużej.

### 1.4 Zdarzenia i transport
- `ChatEvents.cs`: `StageEvent(string Stage, string Label, int? Count)`, `ReasoningDeltaEvent(string Text)`.
- `ChatService`: `Progress<RetrievalStage>` i `OnReasoningDelta` piszą do kanału z 1.1.
- `/api/chat` (`Program.cs:226`): mapowanie nowych zdarzeń na zdarzenia SSE.

### 1.5 UI (`Chat.razor`)
- Linia etapu pod bąblem odpowiedzi z licznikiem sekund; etykiety z liczbami („Szukam w bazie
  (7,4 mln fragmentów)…", „Oceniam trafność 50 kandydatów…", „Sprawdzam nowelizacje…",
  „Piszę odpowiedź…"). Czyszczona na `DoneEvent`.
- Akordeon rozumowania (`Chat.razor:95-99`): dopisywanie delt, otwarty gdy `_busy`, zwijany
  po `DoneEvent`.

### 1.6 Testy fazy
Kolejność etapów; brak `Progress` (eval, `/api/search`) nie zmienia niczego; `ReasoningSplitter`
składa całość identycznie (test równoważności); kanał wydrenowany przed `DoneEvent`.

---

# Faza 2 — Router intencji (preprocesor, PRZED retrievalem)

**Co dowozi:** „siema" nie uruchamia retrievalu i odpowiada w ~2–3 s, z jawnym oznaczeniem,
że odpowiedź nie jest oparta na źródłach. Pytania prawne — bez żadnej zmiany ścieżki.

**Architektura:** router działa **w naszym kodzie, przed wywołaniem retrievalu** — nie wymaga
tool callingu ani żadnego wsparcia po stronie serwera modeli. Decyzję podejmuje lekki model,
ale WYMUSZENIE decyzji jest nasze: kod stosuje orzeczenie routera tylko wtedy, gdy przechodzi
bezpiecznik (2.3).

### 2.1 Rejestracja modelu pomocniczego
- `AuxLlmOptions` (sekcja `Llm:Aux`: `BaseUrl`, `Model`, `TimeoutSeconds` — domyślnie krótki,
  np. 10 s).
- `LlmServiceCollectionExtensions`: nazwany `HttpClient "aux"` +
  `AddKeyedSingleton<ILlmProvider>("aux", …)` konstruujący `OpenAiCompatibleLlmProvider`
  z `Options.Create(new LocalLlmOptions{…})`. Rejestracja typowana
  `AddHttpClient<ILlmProvider, …>` jest zajęta przez model główny — stąd usługa kluczowana.
- **Skończony timeout** — główny klient ma `Timeout.InfiniteTimeSpan`; dla pomocniczego to
  byłby błąd: ma być szybki albo żaden.

### 2.2 `IIntentRouter` w `PrawoRAG.Domain`
- Wejście: pytanie + historia tury. Wyjście:
  `RouteDecision { PotrzebnePrzepisy: bool, Zapytanie: string?, Uzasadnienie: string }`.
- Implementacja na modelu pomocniczym: krótki prompt klasyfikacyjny z wymuszonym formatem
  JSON, bez rozumowania. Prompt instruowany ASYMETRYCZNIE (decyzja przekrojowa 4): przy
  jakiejkolwiek wątpliwości `PotrzebnePrzepisy = true`.
- `Zapytanie`: router przy okazji proponuje zapytanie do bazy (może poprawić literówki,
  rozwinąć potoczne sformułowanie) — ale w tej fazie jest tylko logowane, NIE podmienia
  wejścia retrievalu. Podmiana to zmiana jakościowa wymagająca osobnej weryfikacji; wejdzie
  z Fazą 5, gdzie zapytanie i tak formułuje model.
- Awaria/timeout/nieparsowalny JSON ⇒ `PotrzebnePrzepisy = true` (decyzja przekrojowa 3).

### 2.3 Bezpiecznik jednokierunkowy (deterministyczny, nasz kod)
`LegalTokenDetector` w `PrawoRAG.Domain`, złożony z **istniejących** parserów (`CitationParser`,
`ActAliases`, `CaseNumberKey`, `AcronymDetector`): jeżeli w wiadomości jest jakiekolwiek
odwołanie prawne (artykuł, paragraf, sygnatura, numer Dz.U., nazwa/akronim aktu) — retrieval
jest wymuszony **niezależnie od orzeczenia routera**. Ta reguła nie umie orzec, że szukać nie
trzeba — umie tylko wymusić szukanie, więc nie da się jej przeciążyć nowymi intencjami.

### 2.4 Ścieżka bez retrievalu
- Własny, krótki prompt systemowy dla Gemmy (kim jest system, co umie, czego nie robi,
  z jawnym „nie udzielam porad prawnych bez źródeł") — osobna stała, nie doklejka do
  `GroundedPrompt.SystemPrompt` (precedens DOC-2: warunkowe reguły zamiast wspólnego worka).
- Bez `GroundedPrompt`, bez bramki abstynencji, bez walidatora cytowań — nie ma tu źródeł
  ani cytowań do walidowania.
- `NoRetrievalEvent(string Reason)` w `ChatEvents.cs` + jawna, niezwijalna etykieta w UI:
  „nie przeglądałem bazy — ta odpowiedź nie jest oparta na źródłach".
- Odpowiedź small-talkowa NIE zapisuje `RetrievedSources`; `MessageEntity` dostaje nową kolumnę
  `Route` (string: `retrieval` / `smalltalk`) + migracja — bez tego raport z Fazy 6 nie odróżni
  ścieżek.

### 2.5 Wpięcie
- `ChatService.AskAsync`: router + bezpiecznik PRZED `FollowUpSelector.SelectAsync`.
  `/api/chat` i eval przechodzą przez to samo wejście (decyzja przekrojowa 1).
- Flaga `Retrieval:RouterEnabled` (domyślnie `false` do czasu integracji całości; wyłączona =
  dzisiejsze zachowanie bajt w bajt).
- Etap routera widoczny w UI dzięki Fazie 1: „Rozpoznaję pytanie…".

### 2.6 Testy fazy
Wszystkie 30 zamrożonych pytań `--refusals` ⇒ `PotrzebnePrzepisy = true` (kontrola negatywna
obowiązkowa); przypadki graniczne: „co z art. 5?" (krótkie, ale prawne — łapie bezpiecznik nawet
przy złym orzeczeniu routera), „dzięki, a co z terminem?" (podziękowanie + pytanie ⇒ retrieval);
router padł ⇒ retrieval; leksykon pozytywny small-talku (kilkanaście wariantów powitań,
podziękowań, pytań o system); ścieżka small-talk pomija obie bramki i emituje `NoRetrievalEvent`.

---

# Faza 3 — Bramka anty-fabrykacji

**Co dowozi:** odpowiedź powołująca się na artykuł/sygnaturę nieobecne w dostarczonym kontekście
nie wychodzi do użytkownika w tej postaci.

**Punkt wyjścia:** `CitationValidator` już to wykrywa (`ArticleRegex`, `CaseNumberRegex` →
`SuspiciousReferences`, testy `AbstentionAndCitationTests.cs:90-92`), ale `IsClean` napędza tylko
badge ✓/⚠ (`Chat.razor:78`) — odpowiedź z wymyślonym artykułem wychodzi, tylko z ostrzeżeniem.

### 3.1 Rozdzielenie i normalizacja sygnału
- `CitationCheck`: rozdzielić `SuspiciousReferences` na `SuspiciousArticles` (zaszumione —
  szeroki regex) i `SuspiciousCaseNumbers` (wysokoprecyzyjne).
- `ContainsNormalized`: przy braku dosłownego trafienia artykułu porównać rdzeń numeru,
  odcinając `ust.`/`§`/`pkt` — inaczej „art. 5 ust. 1" nie dopasuje się do „Art. 5. 1."
  i wygeneruje fałszywy alarm.

### 3.2 `AnswerGate` w `PrawoRAG.Domain`
- Podejrzane odwołania ⇒ **jedna** regeneracja na TYM SAMYM kontekście z instrukcją korygującą
  („poprzednia odpowiedź powołała się na odwołania nieobecne w źródłach: …; cytuj wyłącznie
  z podanych źródeł").
- Po regeneracji nadal podejrzane ⇒ odmowa z podaną przyczyną.
- Bez dodatkowej rundy retrievalu — to Faza 4; mechanizmy się nie nakładają.

### 3.3 Wpięcie, telemetria, UI
- `ChatService`: po `CitationValidator.Validate` decyzja `AnswerGate`;
  `RegeneratingEvent(string Reason)` widoczny dzięki Fazie 1.
- Flaga `Grounding:CitationGateEnabled` (domyślnie `true`; wyłączona = dzisiejsze zachowanie).
- `MessageEntity`: kolumna `Regenerated` (bool) + migracja (jedna migracja wspólna z `Route`
  z Fazy 2, jeśli fazy idą razem); zapis w `ConversationStore`.
- Badge ⚠ zmienia znaczenie: z „wypuściliśmy brudną odpowiedź" na „odpowiedź była regenerowana" —
  podpis w UI do zmiany.

### 3.4 Testy fazy
Podejrzane odwołanie ⇒ dokładnie jedna regeneracja; czysta druga próba wychodzi; nadal brudna ⇒
odmowa; flaga off = dzisiejsze zachowanie; normalizacja („art. 5 ust. 1" vs „Art. 5. 1.");
wymyślona sygnatura ⇒ zawsze łapana.

---

# Faza 4 — Pętla domykająca lukę (druga runda retrievalu zamiast odmowy)

**Co dowozi:** gdy źródła nie domykają pytania, system nie odmawia od razu — pyta **bazę**
(nie użytkownika!) drugi raz, zapytaniem przełożonym przez model pomocniczy na terminologię
ustawową, i dopiero potem stosuje bramkę.

**Dlaczego bezpieczne:** pętla może tylko DODAĆ kontekst; bramka abstynencji i walidator działają
na końcu bez zmian. Zamienia „odmowę" na „odmowę po drugiej próbie" albo odpowiedź ugruntowaną —
zero nowego trybu halucynacji.

**Dlaczego tanie:** koszt płacony wyłącznie na pytaniach, których dzisiejszy wynik jest
bezwartościowy; udane odpowiedzi nie zwalniają ani o milisekundę — dlatego 35 s rerankera
tej fazy nie blokuje.

**W co celuje:** `DIAGNOZA-BM25-POLSKI-2026-08-15.md` §9 — `uodo-107`/`uodo-60` to
niedopasowanie terminologii prawnej (synonim, nie forma słowa), wymagające „wnioskowania LLM
nad dobrze dobranym kontekstem". Przypadki zmierzone i nazwane, nie hipotetyczne.

### 4.1 `IQueryReformulator` w `PrawoRAG.Domain`
Implementacja na modelu pomocniczym z 2.1 (ta sama rejestracja — jedna zależność, nie dwie):
z pytania użytkownika jedno zapytanie terminologią ustawową. Awaria/timeout/puste ⇒ `null` ⇒
dzisiejsza odmowa. Nigdy wyjątek na ścieżce czatu.

### 4.2 `GapClosingRetrieval` w `PrawoRAG.Domain` — jedno wejście retrievalu
1. Runda 1 = dzisiejszy `FollowUpSelector.SelectAsync`.
2. `AbstentionPolicy.ShouldAbstain` ⇒ przeformułowanie ⇒ runda 2.
3. Scalenie: suma fragmentów obu rund, deduplikacja po identyfikatorze fragmentu, kolejność
   z istniejącego rerankera + `GroundedPrompt.OrderForGrounding`, **przycięcie do `TopK`**
   (prompt nie może puchnąć).
4. Bramka i walidator na sumie, bez zmian.

### 4.3 Drugi wyzwalacz: odmowa treściowa
Odmowa z reguły 3 promptu nie przechodzi przez bramkę — siedzi w treści. Po generacji: odpowiedź
zawiera dosłowną frazę `AbstentionPolicy.Message` (stała, nie skopiowany napis) ⇒ przeformułowanie,
druga runda, jedna regeneracja. **Ważniejszy z dwóch wyzwalaczy** — odmowy są treściowe, nie progowe.

### 4.4 Limit przekrojowy
Maksymalnie **jedna** dodatkowa runda retrievalu i **jedna** regeneracja na turę, licząc łącznie
z regeneracją z Fazy 3 — inaczej mechanizmy się kumulują i tura puchnie do minut. Licznik w jednym
miejscu.

### 4.5 Konfiguracja, widoczność, eval
- Flagi: `Retrieval:GapClosingEnabled` (domyślnie `true`), `Retrieval:MaxExtraRounds = 1`.
- `RetryingRetrievalEvent(string NewQuery, string Reason)` → UI: „źródła nie domykały pytania —
  szukam inaczej: «…»".
- `RefusalEvalRunner` + `Program.cs` evalu przechodzą na `GapClosingRetrieval` — metryka odmów
  mierzy realny pipeline.

### 4.6 Testy fazy
Każdy wyzwalacz z osobna ⇒ dokładnie jedna dodatkowa runda; oba naraz ⇒ nadal jedna; awaria
przeformułowania ⇒ dzisiejsze zachowanie; udana runda 1 ⇒ zero dodatkowych rund (dowód braku
regresji latencji); scalenie przycięte do `TopK`; deduplikacja.

---

# Faza 5 — Retrieval jako narzędzie (`szukaj_w_przepisach`)

**Co dowozi:** Gemma formułuje zapytania do bazy sama, przez tool calling — poprawia jakość
zapytań (model widzi historię i wie, czego szuka) i otwiera drogę do pracy agentowej.
Router z Fazy 2 zostaje jako pierwsza linia (small-talk nadal nie dociera ani do Gemmy
z narzędziami, ani do bazy).

**Ryzyko, które ta faza wprowadza:** model uznaje, że zna odpowiedź, nie woła narzędzia
i halucynuje z pamięci parametrycznej. Zamknięte w kodzie, nie w promptcie — punkty 5.2 i 5.3.

### 5.1 Warstwa providera
- `LlmRequest`: `Tools` + `ToolChoice`.
- `OpenAiCompatibleLlmProvider`: pola `tools`/`tool_choice` w `ApiRequest`; parsowanie delt
  `tool_calls`; wynik narzędzia jako wiadomość roli `tool`.
- Definicja: `szukaj_w_przepisach(zapytanie: string)`.

### 5.2 Wymuszenie pierwszego wywołania
`tool_choice: "required"` na pierwszym żądaniu tury (konfigurowalne `Llm:ToolChoice`).
Model nie decyduje, CZY szukać — decyduje, CZEGO szukać. Jedyna ścieżka bez narzędzia to
small-talk orzeczony przez router z Fazy 2 i przepuszczony przez bezpiecznik.

### 5.3 Degradacja przy braku wsparcia
Serwer odrzuca `tools`/`tool_choice` (4xx) ⇒ jednorazowy log, oznaczenie możliwości jako
niedostępnej na czas życia procesu, zejście na ścieżkę Faz 1–4 (router + bezwarunkowy retrieval).
**Brak wsparcia nigdy nie oznacza odpowiedzi bez źródeł** — degradacja idzie w stronę większego
bezpieczeństwa. (Gemma historycznie miewała tool calling realizowany promptem, nie natywnym
polem API — wsparcie zależy od stosu serwującego; kod musi działać w obu światach.)

### 5.4 Pętla narzędzia
`ToolLoop` w `PrawoRAG.Domain`: limit `Retrieval:MaxToolCalls` (domyślnie 2); każde wywołanie
idzie przez `GapClosingRetrieval` z Fazy 4 (domykanie luki działa też tutaj); wyniki wracają
jako wiadomość narzędzia; etapy raportowane kanałem z Fazy 1. Zapytanie z narzędzia przechodzi
przez te same tory dokładne i bramki co dziś.

### 5.5 Konfiguracja
`Retrieval:ToolCallingEnabled` (domyślnie `false` do integracji), `Retrieval:MaxToolCalls`,
`Llm:ToolChoice`.

### 5.6 Testy fazy
Pierwsze wywołanie wymuszone; limit wywołań; odrzucenie `tools` ⇒ degradacja do Faz 1–4, nigdy
odpowiedź bez źródeł; odpowiedź merytoryczna bez ani jednego wywołania narzędzia (poza
small-talkiem) = błąd testu; wynik narzędzia przechodzi bramkę abstynencji i walidator.

---

# Faza 6 — Raport z żywego ruchu

**Co dowozi:** metryka nadrzędna (odsetek odmów) i działanie routera oraz obu bramek policzone
na realnym ruchu — także wstecz, na wszystkim, co przeszło przez system od lipca.

**Punkt wyjścia:** `MessageEntity` od migracji `20260707123621` trzyma `Abstained`,
`CitationClean`, `RetrievedSources`, `Model`, `CreatedAt`, `Feedback` (komentarz encji:
„zapisujemy KONTEKST decyzji — to materiał do golden setu i kalibracji"). Fazy 2–3 dokładają
`Route` i `Regenerated`. Nie trzeba nic instrumentować — trzeba policzyć.

### 6.1 `--live-report` w `PrawoRAG.Eval`
Nad tabelą `messages`, per dzień i per model:
- odsetek odmów **progowych** (`Abstained == true`) i **treściowych** (fraza
  `AbstentionPolicy.Message` w `Content` przy `Abstained == false`) — osobno;
- odsetek `CitationClean == false` i `Regenerated == true`;
- rozkład `Route` (ile ruchu porusza się bez bazy) — trafność routera do przeglądu ręcznego;
- treść pytań z grup odmów — materiał do golden setu.

### 6.2 Wyjście i testy
JSONL do `logs/` (wzorem istniejących runnerów) + podsumowanie na konsoli; zero wywołań LLM.
Testy: zliczanie na zasianym zestawie; odmowa treściowa ≠ progowa; wiadomości użytkownika poza
mianownikiem.

---

# Testy manualne (po zintegrowaniu całości, maszyna z pełnym korpusem)

### T1. Widoczność
Zwykłe pytanie prawne. **Oczekiwane:** pierwszy etap <2 s; etapy zmieniają się z licznikiem
sekund; rozumowanie rośnie w trakcie generacji.

### T2. Follow-up pokazuje dwa przebiegi
Pytanie + dopytanie („a co z § 2?"). **Oczekiwane:** widoczne przebiegi „surowy" i „kontekstowy" —
wyjaśnienie podwójnego czasu.

### T3. Small-talk pomija bazę
„siema", „dzięki", „co potrafisz?". **Oczekiwane:** odpowiedź <3 s, etykieta „nie przeglądałem
bazy — ta odpowiedź nie jest oparta na źródłach", zero etapów retrievalu.

### T4. Pytanie prawne w luźnej formie NIE pomija bazy — najważniejszy test zestawu
„hej, a jak z rozwodem bez orzekania o winie?", „co z art. 5?", „dzięki, a co z terminem?".
**Oczekiwane:** za każdym razem pełny retrieval. Tu leży ryzyko, którego dziś w systemie nie ma.

### T5. Bezpiecznik jednokierunkowy
Skieruj `Llm:Aux:BaseUrl` na nieistniejący adres (router padnie). Zadaj pytanie z sygnaturą
i z „art. …". **Oczekiwane:** retrieval dla wszystkiego — awaria routera nie zmienia zachowania
względem dzisiejszego.

### T6. Bramka anty-fabrykacji — regeneracja
Pytanie, które historycznie dawało ⚠. **Oczekiwane:** widoczny `RegeneratingEvent`; końcowa
odpowiedź czysta albo odmowa z przyczyną — nigdy odpowiedź z wymyślonym artykułem.

### T7. Bramka nie psuje dobrych odpowiedzi
10 pytań, które dziś dają dobre odpowiedzi. **Oczekiwane:** zero regeneracji. Regeneracja =
fałszywy alarm normalizacji (3.1) i powód do zawężenia detektora.

### T8. Pętla domykająca na udokumentowanych przypadkach
`uodo-107` i `uodo-60` z golden setu. **Oczekiwane:** „szukam inaczej: «…»" z widocznym
przeformułowaniem, potem odpowiedź ugruntowana zamiast odmowy. Jeśli nadal odmowa — log
przeformułowania rozstrzyga, czy problem jest w modelu pomocniczym, czy w korpusie.

### T9. Pętla nie odpala się bez potrzeby
5 pytań odpowiadających dobrze. **Oczekiwane:** zero dodatkowych rund, czas bez zmian.

### T10. Pętla nie ratuje się w nieskończoność
Pytanie o akt nieobecny w korpusie. **Oczekiwane:** dokładnie jedna dodatkowa runda, potem odmowa.

### T11. Tool calling i degradacja
Przy włączonym `ToolCallingEnabled`: pytanie prawne ⇒ widoczne wywołanie narzędzia przed
odpowiedzią. Przy serwerze bez wsparcia `tools`: jednorazowy log i płynne zejście na ścieżkę
router + bezwarunkowy retrieval; żadna odpowiedź merytoryczna bez źródeł.

### T12. Raport zgadza się z historią
`--live-report`, wybrane wiersze sprawdzone w panelu historii czatu. **Oczekiwane:** odmowy
treściowe osobno od progowych; rozkład `Route` zgodny z tym, co widać w UI.

---

# Metody pomiaru

Przed i po integracji, na tych samych wejściach.

| co mierzymy | jak | wartość docelowa |
|---|---|---|
| **Odsetek odmów** (metryka nadrzędna) | `--refusals` (zamrożone 30) ORAZ `--live-report` na historii `messages` | spadek; kierunek rozstrzyga żywy ruch, nie zamrożony zestaw |
| **Trafność routera — strona krytyczna** | 30 zamrożonych pytań + zestaw z T4: % skierowanych do retrievalu | **100 %**; jakikolwiek błąd ⇒ zawężamy leksykon small-talku / oddajemy decyzję modelowi głównemu — nie stroimy prefiltru w kółko |
| **Trafność routera — strona oszczędnościowa** | % small-talku skierowanego poza bazę (przegląd ręczny próbki z `--live-report`, kolumna `Route`) | wysoki, ale to strona TANIA pomyłki — nie poświęcamy dla niej strony krytycznej |
| **Wyciek halucynacji** | % odpowiedzi z niepustymi `SuspiciousArticles`/`SuspiciousCaseNumbers`, które dotarły do użytkownika | zero |
| **Fałszywe alarmy bramki** | % `Regenerated == true`, gdzie pierwsza wersja była poprawna (przegląd ręczny próbki) | <10 %; wyżej ⇒ zawęzić normalizację 3.1 |
| **Skuteczność pętli** | % dzisiejszych odmów zamienionych w odpowiedź ugruntowaną | <20 % ⇒ luka w korpusie albo słownik synonimów — nie dokładamy trzeciej rundy |
| **Pominięcia retrievalu** | % odpowiedzi merytorycznych bez ani jednego wywołania retrievalu (poza `Route = smalltalk`) | zero |
| **Brak regresji latencji** | `PRAWORAG_LOG_TIMING=1`, 5 dobrych pytań, przed/po | zero dodatkowych rund, czas bez zmian |
| **Koszt ścieżek naprawczych** | `PRAWORAG_LOG_TIMING=1` na pytaniach wyzwalających pętlę/regenerację | znany, zaraportowany, płacony tylko tam |
| **Odczuwana latencja** | czas do pierwszego sygnału w UI; czas odpowiedzi small-talku | <2 s; <3 s |

**Zasada interpretacji:** zamrożone 30 pytań służy WYŁĄCZNIE do wykrywania regresji — strojenie
promptów i progów pod ten zestaw to przeuczenie na 30 pytaniach. Kierunek rozstrzyga raport
z żywego ruchu.

---

# Pipeline docelowy (po Zadaniu 17, wszystkie flagi ON)

Jedna tura czatu, od wiadomości użytkownika do zapisu w historii. Po prawej zdarzenia
widoczne dla użytkownika (Faza 1 sprawia, że KAŻDY etap jest widoczny w <2 s).

```
wiadomość użytkownika
        │
        ▼
┌─ 1. LegalTokenDetector (deterministyczny, nasz kod) ────────────────┐
│  odwołanie prawne (art./§/Dz.U./sygnatura/akronim/nazwa aktu)?      │
│  TAK ⇒ wymuszony retrieval, routera w ogóle nie wołamy ──────► (3)  │
└──────────────────────────────┬──────────────────────────────────────┘
                        NIE    │                        StageEvent: „Rozpoznaję pytanie…"
                               ▼
┌─ 2. IIntentRouter (Bielik 11B, timeout 10 s) ───────────────────────┐
│  awaria/timeout/śmieciowy JSON ⇒ PotrzebnePrzepisy=true (fail-safe) │
│  PotrzebnePrzepisy=false ⇒ ŚCIEŻKA SMALL-TALK:                      │
│    SmalltalkPrompt → Gemma streamuje → NoRetrievalEvent             │
│    bez GroundedPrompt, bez OBUDWU bramek, Route="smalltalk"         │
│    UI: „nie przeglądałem bazy — odpowiedź nie jest oparta           │
│    na źródłach"  ⇒ KONIEC TURY (~2–3 s)                             │
└──────────────────────────────┬──────────────────────────────────────┘
                     przepisy  │
                               ▼
┌─ 3. Pozyskanie kontekstu ───────────────────────────────────────────┐
│                                                                     │
│  wariant A (tool calling sprawny):                                  │
│    Gemma + tools=[szukaj_w_przepisach], tool_choice=REQUIRED        │
│    ⇒ model NIE MOŻE odpowiedzieć bez wywołania; formułuje własne    │
│      zapytanie ⇒ ToolLoop (max 2 wywołania) ⇒ każde wywołanie       │
│      przechodzi przez (3a)                                          │
│                                                                     │
│  wariant B (serwer odrzucił tools ⇒ degradacja, log jednorazowy):   │
│    (3a) wołane wprost z pytaniem użytkownika — ścieżka klasyczna    │
│                                                                     │
│  ┌─ 3a. GapClosingRetrieval (jedno wejście: czat, /api/chat, eval) ─┐
│  │ runda 1 = FollowUpSelector (follow-up ⇒ 2 pełne przebiegi:      │
│  │   surowy vs kontekstowy, oba widoczne)                          │
│  │   każdy przebieg: embed → dense(HNSW halfvec, ef 1000) →        │
│  │   sparse BM25 → tor akronimowy → tory dokładne (sygnatura /     │
│  │   akt Dz.U. / artykuł) → fetch → rerank.main (cross-encoder)    │
│  │   → fuzja → most cytowań → rerank.bridge → top K               │
│  │                              StageEvent per etap, z liczbami    │
│  │ AbstentionPolicy na wyniku rundy 1:                             │
│  │   abstain ⇒ IQueryReformulator (Bielik: terminologia ustawowa)  │
│  │   ⇒ runda 2 ⇒ scalenie+dedup+rerank ⇒ przycięcie do TopK        │
│  │   (reformulator padł/nic nie zmienił ⇒ null ⇒ bez rundy 2)      │
│  │                    RetryingRetrievalEvent: „szukam inaczej: «…»"│
│  └──────────────────────────────────────────────────────────────────┘
└──────────────────────────────┬──────────────────────────────────────┘
                               ▼
   4. BRAMKA ABSTYNENCJI (bez zmian: próg 0,55 / ExactMatchHits)
      abstain po wyczerpaniu budżetu ⇒ AbstainEvent ⇒ KONIEC
                               │
                               ▼
   5. Augmenter (markery nowelizacji) → OrderForGrounding →
      GroundedPrompt.Build ⇒ SourcesEvent (panel źródeł [n])
                               │
                               ▼
   6. GENERACJA (Gemma 4 31B, streaming)
      ReasoningDeltaEvent na żywo (akordeon rośnie) + TokenEvent
                               │
                               ▼
┌─ 7. Kontrole po generacji (WSPÓLNY BUDŻET NAPRAWCZY TURY:          ─┐
│     max 1 dodatkowa runda retrievalu + max 1 regeneracja, łącznie)  │
│                                                                     │
│  7a. odmowa TREŚCIOWA (fraza AbstentionPolicy.Message w treści)?    │
│      ⇒ jeśli budżet: reformulacja → druga runda → regeneracja       │
│                                                                     │
│  7b. CitationValidator → AnswerGate:                                │
│      podejrzany artykuł/sygnatura (nieobecne w kontekście)          │
│      ⇒ jeśli budżet: RegeneratingEvent → regeneracja na TYM SAMYM   │
│        kontekście z instrukcją korygującą → ponowna walidacja       │
│      ⇒ nadal brudna: ODMOWA z przyczyną                             │
│        (halucynowane odwołanie NIGDY nie wychodzi)                  │
└──────────────────────────────┬──────────────────────────────────────┘
                               ▼
   8. DoneEvent (model, CitationCheck, usage) ⇒ zapis do historii:
      Content, RetrievedSources, Abstained, CitationClean,
      Route (retrieval/smalltalk), Regenerated
      ⇒ to zasila raport --live-report (Faza 6)
```

**Trzy niezmienniki pipeline'u** (egzekwowane kodem i testami, nie promptem):
1. Odpowiedź merytoryczna istnieje TYLKO za retrievalem — small-talk jest jawnie oznaczony,
   degradacja tool callingu prowadzi do retrievalu klasycznego, awaria routera do retrievalu.
2. Odwołanie do artykułu/sygnatury nieobecnych w kontekście nie opuszcza systemu (AnswerGate).
3. Mechanizmy naprawcze mają wspólny, twardy budżet na turę — tura nie może puchnąć w pętlę.

**Profil latencji po zmianach:**
- small-talk: ~2–3 s (dziś ~85 s + odmowa);
- pytanie prawne, runda 1 trafia: jak dziś (~85 s), ale od <2 s widać etapy i rozumowanie;
- pytanie dziś kończące się odmową: +1 runda retrievalu (+~40 s) z szansą na odpowiedź
  zamiast odmowy — koszt płacony tylko tam, gdzie dziś wynik był bezwartościowy.

## Budżet wywołań Gemmy i polityka thinking

Pomiar bazowy: ~45 s `llm.total`, z czego ~41 s to rozumowanie. Każde DODATKOWE wywołanie Gemmy
z pełnym thinkingiem kosztuje więc rząd +40 s — to najdroższa pojedyncza operacja w systemie,
droższa niż runda retrievalu. Stąd trzy reguły:

**R1. Zwykłe pytanie = JEDNO wywołanie Gemmy, jak dziś.** Dla pojedynczego pytania
(„Czy aplikant adwokacki może zastępować radcę prawnego?") tool calling nie dodaje wartości —
zapytanie, które model by sformułował, jest praktycznie tożsame z pytaniem użytkownika —
a dodaje jedno pełne wywołanie (sformułowanie tool calla), czyli potencjalnie 2× thinking.
Dlatego **wariant B (klasyczny) jest domyślny także PO integracji**: `Retrieval:ToolCallingEnabled`
zostaje `false` w Zadaniu 17. Tool calling (Zadania 14–15) jest zaimplementowany i przetestowany,
ale włączany dopiero, gdy raport z żywego ruchu pokaże klasę pytań, w której iteracja modelu
(drugie, doprecyzowane wywołanie narzędzia) realnie ratuje odpowiedzi — to scenariusz przyszłej
pracy agentowej, nie typowego Q&A.

**R2. Thinking tylko tam, gdzie pracuje.** Rozumowanie jest potrzebne przy PISANIU odpowiedzi
(zastosowanie przepisu do stanu faktycznego). Nie jest potrzebne przy: sformułowaniu wywołania
narzędzia, regeneracji po `AnswerGate` (korekta mechaniczna: „usuń odwołanie X"), small-talku.
Implementacyjnie: `LlmRequest` dostaje opcję ograniczenia rozumowania; jeżeli stos serwujący
wspiera sterowanie thinkingiem per żądanie — używamy go; niezależnie od tego wywołania
nie-odpowiedziowe dostają NISKI `MaxTokens` (ucięcie siłowe) i `Temperature = 0`.
Dotyczy Zadań 8 (small-talk), 10 (regeneracja), 15 (tool call).

**R3. Bilans tokenów — mierzony, nie zakładany.** Oczekiwany kierunek per ścieżka:
small-talk = duży spadek (dziś pełny prompt ze źródłami idzie do kosza na odmowę);
zwykłe pytanie w wariancie B = neutralnie (+~100 tokenów routera na Bieliku);
ścieżki naprawcze = wzrost płacony wyłącznie na pytaniach dziś bezwartościowych.
`LlmUsage` jest już zbierane per odpowiedź — raport `--live-report` (Zadanie 16) dostaje
kolumnę sumy tokenów per `Route`, żeby bilans był liczbą, nie opinią. Koszt pieniężny przy
stawkach self-host/Sherlock (€0,56/1M) jest pomijalny — walutą decyzji jest czas użytkownika.

# Rozbicie implementacyjne

## Status implementacji (2026-08-24)

**Fazy 1–4 ZROBIONE — Zadania 1–13, 627/627 testów zielone** (było 493 przed sesją; +134 nowe).
Commity `49b2586`..`d9501b0` na `feat/halfvec-retriever`.

Stan flag: `RouterEnabled` OFF (czeka na weryfikację E2E — jedyna zmiana mogąca dać odpowiedź bez
źródeł), `CitationGateEnabled` ON, `GapClosingEnabled` ON z `MaxExtraRounds=1`. Dwie ostatnie są
włączone, bo poprawiają bezpieczeństwo albo mogą tylko DODAĆ kontekst — żadna nie wnosi nowego
trybu halucynacji.

Zostają Zadania 14–17: tool calling (`tools`/`tool_choice` + `ToolLoop`), raport `--live-report`,
integracja z testami manualnymi i pomiarami.

Znaleziska z implementacji, których plan nie przewidział (wszystkie złapane testami, nie lekturą):

1. **`ReasoningSplitter` rozpoznaje rozumowanie w DWÓCH trybach** (flaga `google.thought` ORAZ gołe
   tagi `<think>`), a plan zakładał podpięcie callbacku po samej fladze. Podpięcie w `Route` splittera
   obsługuje oba i oddaje treść bez tagów-delimiterów.
2. **`Progress<T>` z BCL dyspozycjonuje callbacki ASYNCHRONICZNIE** — etapy retrievalu docierały PO
   etapie „Piszę odpowiedź" i w losowej kolejności (zmierzone: `augment, llm, embed, rerank.main`).
   Powstał `SyncProgress<T>`. Ten sam błąd dotyczył SSE (fire-and-forget `Task.Run` per zdarzenie) —
   zamieniony na kolejkę z jedną pompą, domykaną przed `done`.
3. **`CitationParser.Parse` zwraca `CitationRef` tylko gdy jest ARTYKUŁ** — goła nazwa aktu
   („ustawa o ochronie danych osobowych", „ordynacja podatkowa") i goły paragraf nie były wykrywane.
   Prywatna `ActHint` wystawiona jako `ExtractActHint`, zamiast pisać drugą kopię wzorców.
4. **Kolizja nazw w Blazorze:** stałe `Route*` nie mogą stać na `ChatService`, bo w `Chat.razor`
   ta nazwa jest zajęta przez wstrzykniętą właściwość → osobna klasa `ChatRoutes`.
5. **Licznik sekund wymaga tickera 1 s** — bez niego czas zamarza między zdarzeniami, a najdłuższy
   etap (~35 s) nie emituje żadnego, czyli zamarzałby dokładnie tam, gdzie dowód życia jest potrzebny.

6. **`CitationParser.Parse` wymaga artykułu** (znalezisko 3) miało bliźniaka w walidatorze: dosłowne
   porównanie odwołania z kontekstem produkowało FAŁSZYWE ALARMY na wariantach zapisu, które
   w aktach są normą — model pisze „art. 5 ust. 1", a tekst jednolity ma „Art. 5. 1.". Bez
   normalizacji bramka z Zadania 10 zawracałaby poprawne odpowiedzi, czyli zamieniałaby halucynacje
   na odmowy. Rdzeń numeru porównywany z granicą, żeby „art. 1" nie zaliczyło się jako pokrycie dla
   „art. 1a" (to inny przepis).
7. **Nazwy `outcome`/`newQuery` kolidowały** z istniejącymi zmiennymi w `RefusalEvalRunner`
   i `ChatService` — drobiazg, ale oba wyszły dopiero z kompilatora, nie z lektury.

> **Dla agentów:** kroki mają checkboxy (`- [ ]`). Kolejność zadań = kolejność commitów; każde
> zadanie zostawia zielony `dotnet test` i działający system (flagi nowych zachowań domyślnie
> OFF do Zadania 17). Testy piszemy PRZED implementacją. Commity: treść ASCII bez polskich
> znaków, trailer `Co-Authored-By` z nazwą modelu. Fake'i `FakeRetriever`, `FakeReranker`
> i wzorce `FakeLlm`/`ScriptedLlm` są w `tests/PrawoRAG.Tests/{Fakes,Chat,Analysis}`.

## Zadanie 1 ✅ ZROBIONE: delty rozumowania z providera (Faza 1)

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Llm/ILlmProvider.cs` (nowe pole w `LlmRequest`, po `OnReasoning`)
- Modify: `src/PrawoRAG.Llm/OpenAiCompatibleLlmProvider.cs` (miejsce `splitter.Push(delta, isThought)`)
- Test: `tests/PrawoRAG.Tests/Llm/ReasoningDeltaTests.cs` (nowy)

**Interfejsy:**
- Produces: `LlmRequest.OnReasoningDelta` (`Action<string>?`, null = zero kosztu, jak `OnUsage`).
- `ClaudeLlmProvider` NIE dotykany (pole opcjonalne).

- [ ] Testy: (a) delty z `isThought` trafiają do `OnReasoningDelta` w kolejności nadejścia;
      (b) `OnReasoning` na końcu dostaje DOKŁADNIE konkatenację delt (równoważność — dowód,
      że zapis do historii się nie zmienia); (c) `OnReasoningDelta == null` ⇒ zachowanie
      identyczne jak dziś (snapshot strumienia widocznego); (d) delty widoczne (nie-thought)
      NIE trafiają do `OnReasoningDelta`. Serwer SSE symulowany `HttpMessageHandler`em ze
      skryptowanym strumieniem.
- [ ] Implementacja: `request.OnReasoningDelta?.Invoke(delta)` obok `splitter.Push`, tylko dla `isThought`.
- [ ] Commit: `feat(llm): OnReasoningDelta - delty rozumowania na zywo z providera OpenAI-compat`

## Zadanie 2 ✅ ZROBIONE: etapy retrievalu przy istniejących punktach LatencyLog (Faza 1)

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Retrieval/Retrieval.cs` (typ `RetrievalStage`, pole w `RetrievalQuery`)
- Modify: `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs` (przy każdym `LatencyLog.Mark`/`TimeAsync`)
- Modify: `src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs` (prefiks `surowy`/`kontekstowy`)
- Test: `tests/PrawoRAG.Tests/Retrieval/RetrievalStageTests.cs` (nowy)

**Interfejsy:**
- Produces: `RetrievalStage(string Name, string Label, int? Count)`;
  `RetrievalQuery.Progress` (`IProgress<RetrievalStage>?`).
- Etap raportowany PRZED pomiarem czasu etapu — użytkownik ma widzieć, CO się zaczyna.

- [ ] Testy: (a) `HybridRetriever` emituje etapy w porządku pipeline'u (`embed` → `dense` → …
      → `rerank.main`) — kolekcja `LiveDb`, jak istniejące testy retrievera; (b) `Progress == null`
      ⇒ wynik retrievalu identyczny (równoważność); (c) `FollowUpSelector` z historią emituje
      etapy OBU przebiegów z rozróżnialnym prefiksem.
- [ ] Implementacja; etykiety PL i liczby (`Count`) tam, gdzie znane (kandydaci, chunki).
- [ ] Commit: `feat(retrieval): IProgress<RetrievalStage> - etapy retrievalu z punktow LatencyLog`

## Zadanie 3 ✅ ZROBIONE: kanał zdarzeń w ChatService + transport SSE (Faza 1)

**Pliki:**
- Modify: `src/PrawoRAG.Api/Services/ChatEvents.cs` (`StageEvent`, `ReasoningDeltaEvent`)
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs` (Channel + podpięcie obu źródeł)
- Modify: `src/PrawoRAG.Api/Program.cs` (mapowanie nowych zdarzeń na SSE w `/api/chat`)
- Test: `tests/PrawoRAG.Tests/Chat/ChatServiceStageEventsTests.cs` (nowy)

**Interfejsy:**
- Produces: `StageEvent(string Stage, string Label, int? Count)`, `ReasoningDeltaEvent(string Text)`.
- Wzorzec: `Channel.CreateUnbounded<ChatEvent>()`; callbacki (`Progress`, `OnReasoningDelta`)
  piszą `TryWrite`; pętla `AskAsync` drenuje kanał przed/po własnych `yield` i w pętli tokenów;
  `Complete()` + pełny drenaż przed `DoneEvent`.

- [ ] Testy: `StageEvent`y PRZED `SourcesEvent`; delty rozumowania przemieszane z `TokenEvent`
      (ScriptedLlm emitujący thought+tekst); po `DoneEvent` kanał pusty (nic nie ginie);
      ścieżka odmowy też emituje etapy. Fake'i jak w `ChatServiceDocumentTests`.
- [ ] Implementacja.
- [ ] Commit: `feat(chat): kanal zdarzen - etapy retrievalu i delty rozumowania w strumieniu SSE`

## Zadanie 4 ✅ ZROBIONE: UI — pasek etapu i rozumowanie na żywo (Faza 1)

**Pliki:**
- Modify: `src/PrawoRAG.Api/Components/Pages/Chat.razor`

- [ ] Linia etapu pod bąblem odpowiedzi: etykieta + licznik sekund (timer komponentu),
      czyszczona na `DoneEvent`/`ChatErrorEvent`.
- [ ] Akordeon rozumowania (`Chat.razor:95-99`): `ReasoningDeltaEvent` dopisuje do `ex.Reasoning`;
      otwarty gdy `_busy`, zwinięty po `DoneEvent`; `ReasoningEvent` (całość) nadpisuje na końcu.
- [ ] Ręczna weryfikacja na dev-bazie (39 orzeczeń wystarczy, żeby zobaczyć etapy).
- [ ] Commit: `feat(ui): pasek etapu pracy + rozumowanie modelu na zywo w akordeonie`

## Zadanie 5 ✅ ZROBIONE: rejestracja modelu pomocniczego (Faza 2)

**Pliki:**
- Create: `src/PrawoRAG.Llm/AuxLlmOptions.cs`
- Modify: `src/PrawoRAG.Llm/LlmServiceCollectionExtensions.cs`
- Modify: `src/PrawoRAG.Api/appsettings.json` (sekcja `Llm:Aux`)
- Test: `tests/PrawoRAG.Tests/Llm/AuxLlmRegistrationTests.cs` (nowy)

**Interfejsy:**
- Produces: klucz DI `"aux"` → `ILlmProvider` (keyed singleton na nazwanym `HttpClient "aux"`,
  `OpenAiCompatibleLlmProvider` z `Options.Create(new LocalLlmOptions { BaseUrl, Model })`).
- `AuxLlmOptions`: `BaseUrl`, `Model` (default Bielik z `LocalLlmOptions`), `TimeoutSeconds = 10`.
- UWAGA: timeout SKOŃCZONY — główny klient ma `Timeout.InfiniteTimeSpan`, tu to byłby błąd.

- [ ] Testy: rejestracja rozwiązuje się z kontenera; timeout klienta = konfigurowany;
      brak sekcji `Llm:Aux` ⇒ wartości domyślne (Bielik).
- [ ] Commit: `feat(llm): model pomocniczy - keyed ILlmProvider "aux" z krotkim timeoutem`

## Zadanie 6 ✅ ZROBIONE: LegalTokenDetector — bezpiecznik jednokierunkowy (Faza 2)

**Pliki:**
- Create: `src/PrawoRAG.Domain/Retrieval/LegalTokenDetector.cs`
- Test: `tests/PrawoRAG.Tests/Retrieval/LegalTokenDetectorTests.cs` (nowy)

**Interfejsy:**
- Produces: `LegalTokenDetector.ContainsLegalReference(string text)` → `bool`. Czysta funkcja.
- Consumes: WYŁĄCZNIE istniejące parsery — `CitationParser`, `ActAliases`, `CaseNumberKey`,
  `AcronymDetector`. ZERO nowej listy słów.

- [ ] Testy: pozytywne („co z art. 5?", sygnatura, „Dz.U. 2025 poz. 1815", akronim KSeF,
      nazwa aktu z `ActAliases`); negatywne („siema", „dzięki", „co potrafisz?", „napisz coś
      o kotach"); graniczny: „dzięki, a co z terminem art. 300?" ⇒ true.
- [ ] Commit: `feat(retrieval): LegalTokenDetector - deterministyczny bezpiecznik z istniejacych parserow`

## Zadanie 7 ✅ ZROBIONE: IIntentRouter na modelu pomocniczym (Faza 2)

**Pliki:**
- Create: `src/PrawoRAG.Domain/Llm/IIntentRouter.cs` (+ `RouteDecision`)
- Create: `src/PrawoRAG.Llm/AuxIntentRouter.cs`
- Modify: `src/PrawoRAG.Llm/LlmServiceCollectionExtensions.cs` (rejestracja)
- Test: `tests/PrawoRAG.Tests/Llm/AuxIntentRouterTests.cs` (nowy)

**Interfejsy:**
- Produces: `RouteDecision(bool PotrzebnePrzepisy, string? Zapytanie, string Uzasadnienie)`;
  `IIntentRouter.RouteAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken)`.
- Kontrakt fail-safe: KAŻDA awaria (timeout, wyjątek HTTP, nieparsowalny JSON, pusta odpowiedź)
  ⇒ `PotrzebnePrzepisy = true`. Nigdy wyjątek do wołającego.
- Prompt: klasyfikacja + wymuszony JSON, instrukcja ASYMETRYCZNA (wątpliwość ⇒ przepisy);
  `Zapytanie` proponowane, ale w tej fazie tylko logowane.

- [ ] Testy (ScriptedLlm jako aux): poprawny JSON obu klas; śmieciowe wyjście ⇒ true;
      timeout ⇒ true; wyjątek providera ⇒ true.
- [ ] Commit: `feat(llm): IIntentRouter - lekki model orzeka intencje, fail-safe w strone retrievalu`

## Zadanie 8 ✅ ZROBIONE: ścieżka small-talk w ChatService + telemetria Route (Faza 2)

**Pliki:**
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs` (router+bezpiecznik przed `FollowUpSelector`)
- Modify: `src/PrawoRAG.Api/Services/ChatEvents.cs` (`NoRetrievalEvent(string Reason)`)
- Create: `src/PrawoRAG.Llm/Grounding/SmalltalkPrompt.cs` (osobna stała, NIE doklejka do `GroundedPrompt`)
- Modify: `src/PrawoRAG.Api/Program.cs` (SSE + flaga), `appsettings.json` (`Retrieval:RouterEnabled=false`)
- Modify: `src/PrawoRAG.Storage/Entities/MessageEntity.cs` (`Route` string, `Regenerated` bool)
  + JEDNA migracja dla obu kolumn (`Regenerated` użyje Zadanie 10)
- Modify: `src/PrawoRAG.Api/Services/ConversationStore.cs` (zapis `Route`)
- Modify: `src/PrawoRAG.Api/Components/Pages/Chat.razor` (etykieta „nie przeglądałem bazy…")
- Test: `tests/PrawoRAG.Tests/Chat/ChatServiceRouterTests.cs` (nowy)

**Przepływ w `AskAsync`:** flaga OFF ⇒ dzisiejsza ścieżka bajt w bajt. Flaga ON ⇒
`LegalTokenDetector` (true ⇒ retrieval, routera NIE wołamy — oszczędzamy wywołanie) →
`IIntentRouter` → `PotrzebnePrzepisy=false` ⇒ ścieżka small-talk (SmalltalkPrompt, bez
`GroundedPrompt`, bez bramek, `NoRetrievalEvent`, `Route="smalltalk"`); inaczej retrieval.

**Analiza pism omija router.** `AnalysisRunner` woła ten sam `AskAsync` per jednostkę dokumentu
(`AnalysisRunner.cs:133`) z pytaniem sklejonym z treści jednostki — jednostka BEZ tokenu prawnego
(preambuła, komparycja, dane adresowe) + pomyłka routera dałaby werdykt analizy BEZ retrievalu,
czyli nieugruntowany. Jednostka map-reduce nigdy nie jest small-talkiem: `IChatService.AskAsync`
dostaje parametr `forceRetrieval` (default `false`), `AnalysisRunner` przekazuje `true` — router
i bezpiecznik są wtedy pomijane, retrieval bezwarunkowy jak dziś. Test: jednostka bez tokenów
prawnych + atrapa routera zawsze-false + `forceRetrieval=true` ⇒ retrieval wykonany.

- [ ] Testy: flaga OFF = snapshot dzisiejszego strumienia zdarzeń; token prawny ⇒ router
      niewołany i retrieval idzie; router mówi „nie trzeba" bez tokenu ⇒ `NoRetrievalEvent`,
      ZERO wywołań `IRetriever` (licznik na fake'u), zero `SourcesEvent`, brak walidacji
      cytatów; router pada ⇒ retrieval; odpowiedź small-talk zapisuje `Route="smalltalk"` bez źródeł.
- [ ] Kontrola negatywna: wszystkie pytania zamrożonego zestawu `--refusals` przez
      `LegalTokenDetector` + atrapę routera zawsze-false — każde MUSI skończyć w retrievalu.
- [ ] Commit: `feat(chat): router intencji za flaga - small-talk bez retrievalu, jawnie nieugruntowany`

## Zadanie 9 ✅ ZROBIONE: rozdzielenie i normalizacja sygnału CitationValidator (Faza 3)

**Pliki:**
- Modify: `src/PrawoRAG.Llm/Grounding/CitationValidator.cs`
- Test: `tests/PrawoRAG.Tests/Grounding/AbstentionAndCitationTests.cs` (dopisać)

**Interfejsy:**
- `CitationCheck`: nowe `SuspiciousArticles`, `SuspiciousCaseNumbers`; `SuspiciousReferences`
  zostaje jako suma (czyta go UI i eval), `IsClean` bez zmiany semantyki.
- Normalizacja artykułu: brak dosłownego trafienia ⇒ porównaj rdzeń (`art. N` bez `ust./§/pkt`)
  — „art. 5 ust. 1" musi dopasować się do kontekstu z „Art. 5. 1.".

- [ ] Testy: rozdział sygnałów; 3–4 warianty zapisu z realnych aktów; istniejące testy zielone
      bez modyfikacji asercji.
- [ ] Commit: `refactor(grounding): rozdziel sygnaly walidatora + normalizacja wariantow zapisu artykulu`

## Zadanie 10 ✅ ZROBIONE: AnswerGate — bramka anty-fabrykacji (Faza 3)

**Pliki:**
- Create: `src/PrawoRAG.Llm/Grounding/AnswerGate.cs` (obok `CitationValidator` — konsumuje
  `CitationCheck`, a `PrawoRAG.Domain` nie zna tego typu)
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs`, `ChatEvents.cs` (`RegeneratingEvent`),
  `appsettings.json` (`Grounding:CitationGateEnabled=true`), `ConversationStore.cs`
  (zapis `Regenerated` — kolumna z Zadania 8), `Chat.razor` (podpis badge ⚠ = „regenerowana")
- Test: `tests/PrawoRAG.Tests/Chat/AnswerGateTests.cs` (nowy)

**Interfejsy:**
- Produces: `AnswerGate.Decide(CitationCheck)` → `Pass | Regenerate(string instrukcja) | Refuse(string powod)`.
  Czysta funkcja; pętla regeneracji w `ChatService` (ma dostęp do LLM i kontekstu).
- Regeneracja: TEN SAM kontekst + instrukcja korygująca z listą podejrzanych odwołań.
  Maksymalnie JEDNA (licznik wspólny z Zadaniem 13).

- [ ] Testy: podejrzany artykuł ⇒ `RegeneratingEvent` + drugie wywołanie LLM (ScriptedLlm:
      1. brudna, 2. czysta ⇒ wychodzi czysta); obie brudne ⇒ odmowa z powodem; czysta pierwsza
      ⇒ zero regeneracji; flaga OFF ⇒ dzisiejsze zachowanie; `Regenerated` zapisane.
- [ ] Commit: `feat(grounding): AnswerGate - halucynowane odwolanie nie wychodzi, regeneracja albo odmowa`

## Zadanie 11 ✅ ZROBIONE: IQueryReformulator (Faza 4)

**Pliki:**
- Create: `src/PrawoRAG.Domain/Llm/IQueryReformulator.cs`
- Create: `src/PrawoRAG.Llm/AuxQueryReformulator.cs` (+ rejestracja)
- Test: `tests/PrawoRAG.Tests/Llm/AuxQueryReformulatorTests.cs` (nowy)

**Interfejsy:**
- `ReformulateAsync(string question, CancellationToken)` → `string?`. `null` przy KAŻDEJ
  awarii, pustym wyjściu i wyjściu identycznym z wejściem (identyczne = druga runda byłaby
  deterministycznym powtórzeniem). Model: keyed `"aux"` z Zadania 5.

- [ ] Testy: poprawne przeformułowanie przechodzi; puste/awaria/timeout ⇒ null;
      wyjście == wejście (po trim/case) ⇒ null.
- [ ] Commit: `feat(llm): IQueryReformulator - pytanie na terminologie ustawowa dla drugiej rundy`

## Zadanie 12 ✅ ZROBIONE: GapClosingRetrieval — wyzwalacz progowy (Faza 4)

**Pliki:**
- Create: `src/PrawoRAG.Domain/Retrieval/GapClosingRetrieval.cs`
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs`, `Program.cs` (`/api/chat`),
  `src/PrawoRAG.Eval/RefusalEvalRunner.cs`, `src/PrawoRAG.Eval/Program.cs` — wszyscy przez
  nowe wejście (decyzja przekrojowa 1)
- Modify: `ChatEvents.cs` (`RetryingRetrievalEvent(string NewQuery, string Reason)`),
  `appsettings.json` (`Retrieval:GapClosingEnabled=true`, `Retrieval:MaxExtraRounds=1`)
- Test: `tests/PrawoRAG.Tests/Retrieval/GapClosingRetrievalTests.cs` (nowy)

**Interfejsy:**
- `GapClosingRetrieval.RetrieveAsync(...)` — sygnatura pokrywająca dzisiejsze wywołanie
  `FollowUpSelector.SelectAsync` + `IQueryReformulator` + progi. Zwraca `Selection` + informację
  o rundach (dla zdarzenia i telemetrii).
- Scalenie: suma chunków, dedup po id chunka, porządek rerankera + `OrderForGrounding`,
  przycięcie do `TopK`.

- [ ] Testy: runda 1 ponad progiem ⇒ ZERO wywołań reformulatora (licznik) i jedna runda;
      abstain + udana runda 2 ⇒ scalony wynik, dedup, rozmiar ≤ TopK; abstain + reformulator
      null ⇒ dzisiejsza odmowa; abstain + runda 2 też abstain ⇒ odmowa, dokładnie 2 rundy;
      `MaxExtraRounds=0` ⇒ dzisiejsze zachowanie.
- [ ] Commit: `feat(retrieval): GapClosingRetrieval - druga runda po przeformulowaniu zamiast odmowy progowej`

## Zadanie 13 ✅ ZROBIONE: wyzwalacz treściowy + wspólny licznik naprawczy (Faza 4)

**Pliki:**
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs`
- Test: `tests/PrawoRAG.Tests/Chat/ContentRefusalRetryTests.cs` (nowy)

**Interfejsy:**
- Po generacji: `full.ToString().Contains(AbstentionPolicy.Message)` (STAŁA, nie skopiowany
  napis) ⇒ przeformułowanie ⇒ druga runda ⇒ JEDNA regeneracja na scalonym kontekście.
- **Wspólny budżet naprawczy tury** (jedno miejsce): max 1 dodatkowa runda retrievalu
  i max 1 regeneracja LLM — łącznie dla Zadań 10, 12, 13. Odmowa treściowa po rundzie
  z wyzwalacza progowego NIE odpala kolejnej.

- [ ] Testy: odmowa treściowa ⇒ retry ⇒ odpowiedź z drugiej próby wychodzi; odmowa treściowa
      po odpowiedzi z `GapClosing` (runda już zużyta) ⇒ zero kolejnych rund; regeneracja
      z `AnswerGate` + wyzwalacz treściowy ⇒ budżet respektowany; brak frazy ⇒ zero retry.
- [ ] Commit: `feat(chat): odmowa tresciowa wyzwala druga runde - wspolny budzet naprawczy tury`

## Zadanie 14: tools/tool_choice w warstwie providera (Faza 5)

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Llm/ILlmProvider.cs` (`LlmRequest.Tools`, `ToolChoice`;
  typy `LlmTool`, `LlmToolCall`; callback `OnToolCall` — idiom `OnUsage`)
- Modify: `src/PrawoRAG.Llm/OpenAiCompatibleLlmProvider.cs` (pola w `ApiRequest`, parsowanie
  delt `tool_calls`, wiadomość roli `tool`, degradacja przy 4xx)
- Test: `tests/PrawoRAG.Tests/Llm/ToolCallingProviderTests.cs` (nowy)

**Interfejsy:**
- Degradacja: serwer odrzuca `tools` (4xx) ⇒ jednorazowy log + flaga „niedostępne na czas
  życia procesu" + ponowne żądanie BEZ tools. Wołający dostaje sygnał, że narzędzia nie
  zadziałały — decyzję podejmuje warstwa wyżej (Zadanie 15), nie provider.

- [ ] Testy (HttpMessageHandler ze skryptem): żądanie zawiera `tools`+`tool_choice`; delty
      `tool_calls` składane w `LlmToolCall`; 4xx ⇒ retry bez tools + flaga degradacji;
      `Tools == null` ⇒ ciało żądania bajt w bajt jak dziś (równoważność).
- [ ] Commit: `feat(llm): tool calling w OpenAiCompatibleLlmProvider z degradacja przy braku wsparcia`

## Zadanie 15: ToolLoop — szukaj_w_przepisach (Faza 5)

**Pliki:**
- Create: `src/PrawoRAG.Domain/Llm/ToolLoop.cs`
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs` (gałąź za flagą), `appsettings.json`
  (`Retrieval:ToolCallingEnabled=false`, `Retrieval:MaxToolCalls=2`, `Llm:ToolChoice=required`)
- Test: `tests/PrawoRAG.Tests/Chat/ToolLoopTests.cs` (nowy)

**Interfejsy:**
- Narzędzie `szukaj_w_przepisach(zapytanie)` ⇒ `GapClosingRetrieval` (Zadanie 12) ⇒ wynik jako
  wiadomość `tool`. Pierwsze żądanie tury: `tool_choice=required`. Limit `MaxToolCalls`.
- Żądanie formułujące tool call: NISKI `MaxTokens` + `Temperature=0` (reguła R2 — sformułowanie
  wywołania nie potrzebuje rozumowania; pełny thinking tylko przy pisaniu odpowiedzi).
- Degradacja z Zadania 14 ⇒ ścieżka Faz 1–4 (router + bezwarunkowy retrieval). NIGDY odpowiedź
  merytoryczna bez źródeł.
- Bramki (abstynencja, `AnswerGate`) bez zmian — działają na wyniku niezależnie od tego, czy
  kontekst przyszedł z pętli, czy ze ścieżki klasycznej.

- [ ] Testy: wymuszone pierwsze wywołanie; wynik narzędzia w kontekście odpowiedzi; limit
      wywołań; degradacja ⇒ klasyczna ścieżka (retrieval wykonany); odpowiedź bez wywołania
      narzędzia przy sprawnym tool callingu = FAIL testu; flaga OFF = zero zmian.
- [ ] Commit: `feat(chat): ToolLoop - szukaj_w_przepisach z wymuszonym pierwszym wywolaniem, za flaga`

## Zadanie 16: raport --live-report (Faza 6)

**Pliki:**
- Create: `src/PrawoRAG.Eval/LiveReportRunner.cs`
- Modify: `src/PrawoRAG.Eval/Program.cs` (przełącznik `--live-report`)
- Test: `tests/PrawoRAG.Tests/Eval/LiveReportTests.cs` (nowy, kolekcja LiveDb, zasiany zestaw)

**Interfejsy:**
- Wejście: tabela `messages`. Wyjście: JSONL do `logs/live-report-{ts}.jsonl` + konsola.
- Metryki per dzień i per model: odmowy progowe (`Abstained`), odmowy treściowe
  (`Content.Contains(AbstentionPolicy.Message)` przy `Abstained==false` — STAŁA), odsetek
  `CitationClean==false`, odsetek `Regenerated`, rozkład `Route`, lista pytań z grup odmów,
  suma tokenów in/out per `Route` (bilans z reguły R3 — liczba, nie opinia). Zero wywołań LLM.

- [ ] Testy: zasiane wiadomości ⇒ poprawne zliczenia; odmowa treściowa ≠ progowa; wiadomości
      `user` poza mianownikiem; pusta baza ⇒ pusty raport bez wyjątku.
- [ ] Commit: `feat(eval): --live-report - metryka odmow i bramek policzona na historii messages`

## Zadanie 17: integracja — włączenie flag + pełna weryfikacja

**Pliki:**
- Modify: `src/PrawoRAG.Api/appsettings.json` (`Retrieval:RouterEnabled=true`;
  `Retrieval:ToolCallingEnabled` ZOSTAJE `false` — reguła R1: zwykłe pytanie = jedno wywołanie
  Gemmy; tool calling włączany osobną decyzją, gdy `--live-report` pokaże klasę pytań, którą
  ratuje iteracja modelu)
- Docs: wyniki testów manualnych i pomiarów dopisane do TEGO dokumentu (sekcja statusu)

- [ ] `dotnet test` — komplet zielony.
- [ ] `--refusals` na zamrożonych 30: flagi OFF vs ON — różnice wyłącznie na korzyść
      (odmowa ⇒ odpowiedź ugruntowana); ŻADNA dziś dobra odpowiedź nie może się pogorszyć.
- [ ] Testy manualne T1–T12 (sekcja wyżej) na maszynie z pełnym korpusem; wyniki do dokumentu.
- [ ] Pomiary z tabeli „Metody pomiaru"; przy progach zabicia (router <100 % strony krytycznej,
      fałszywe alarmy bramki >10 %, skuteczność pętli <20 %) — decyzja wg tabeli, nie strojenie.
- [ ] Commit: `feat(chat): wlaczenie routera i tool callingu po pelnej weryfikacji E2E`

## Zależności i możliwa równoległość

```
Z1 ─┬─ Z3 ── Z4                (Faza 1: widoczność)
Z2 ─┘
Z5 ─┬─ Z7 ── Z8                (Faza 2: router; Z6 niezależne)
Z6 ─┘
Z9 ── Z10                      (Faza 3: bramka; Z10 wymaga kolumn z Z8)
Z5 ── Z11 ── Z12 ── Z13        (Faza 4: pętla; Z12 dotyka ChatService po Z8)
Z14 ── Z15                     (Faza 5: tool calling; Z15 wymaga Z12)
Z8+Z10 ── Z16                  (Faza 6: raport czyta Route i Regenerated)
wszystko ── Z17
```
Niezależne ciągi (Z1–Z4), (Z5–Z8), (Z9) mogą iść równolegle; Z10+ po Z8; Z11+ po Z5.
