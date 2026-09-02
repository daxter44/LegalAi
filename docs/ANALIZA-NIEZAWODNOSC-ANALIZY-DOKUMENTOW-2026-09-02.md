# Analiza: niezawodność „Analizy dokumentów" (`/analiza`) — 2026-09-02

Punkt wyjścia: zgłoszenie użytkownika po wgraniu `regulamin.pdf` do analizy — werdykt „?" na
jednym fragmencie, `BŁĄD` na kilku kolejnych (500 z lokalnego LLM, dwa różne błędy transportowe),
fragment całkowicie „nieprzeanalizowany", i seria błędów limitu planu. Ten dokument bada
**dlaczego pipeline się wywraca**, nie czy werdykty są trafne merytorycznie — tamto (jakość
groundingu, retrieval orzecznictwa) jest już opisane w
[RAPORT-JAKOSC-ANALIZY-DOKUMENTOW-2026-07-23.md](RAPORT-JAKOSC-ANALIZY-DOKUMENTOW-2026-07-23.md)
i to osobna, nienachodząca oś problemu — patrz sekcja „Co NIE jest zepsute" niżej.

Metoda: kod (`AnalysisRunner`, `AnalysisStore`, `CostGuard`, `AnalysisPrompts`, `ChatService`,
`ReasoningSplitter`, `OpenAiCompatibleLlmProvider`) + dane produkcyjne z `192.168.100.11` (tabele
`analyses`, `analysis_units`, `usage_counters`) + git blame do datowania. Oznaczenia jak w
poprzednim raporcie: **[FAKT]** = zweryfikowane w kodzie/danych, **[HIPOTEZA]** = prawdopodobne,
nie do końca zamknięte, **[DOMYSŁ]** = wymaga dalszej weryfikacji (np. logów infrastruktury, do
których nie mam dostępu stąd).

## Streszczenie kierownicze

Zgłoszenie użytkownika okazało się **jedną, w pełni zrekonstruowaną sesją** (`01a05ec7`,
`regulamin.pdf`, 1 września 21:01–21:37) — każdy opisany objaw ma dokładne źródło w kodzie:

1. **Limit planu jest współdzielony z czatem i liczony PER JEDNOSTKA, nie per dokument.**
   Dokument o 19 fragmentach = 19+ zapytań z 15-zapytaniowego miesięcznego limitu darmowego —
   jedna analiza może wyczerpać CAŁY miesięczny budżet użytkownika. To dokładnie hipoteza, z
   którą przyszedł użytkownik, i jest ona **potwierdzona co do mechanizmu i co do liczb**
   (15/15 zużyte dokładnie w tej sesji).
2. **Cichy, trwały zanik jednej jednostki** (§ 12 → brak wiersza w DB) — dowód przez eliminację:
   zapis do bazy jest „best-effort" bez logowania; gdy padnie, wynik znika NA ZAWSZE (treść
   dokumentu żyje tylko w pamięci procesu), a UI pokazuje „nieprzeanalizowany" bez żadnego śladu,
   że coś w ogóle się wydarzyło.
3. **Werdykt „?" = model zwrócił PUSTĄ odpowiedź**, nie problem parsowania formatu — potwierdzone
   na 6 niezależnych analizach rozłożonych na 6 tygodni (100% przypadków „?" od czasu naprawy
   innego, historycznego buga z 07-23, ma `Answer` długości 0).
4. **Zero odporności na przejściowe zrywy** (baza danych i LLM) — ani w analizie, ani w czacie.
   Różnica w odczuwalnej awaryjności to **ekspozycja, nie jakość inżynierii**: czat robi ~1
   wywołanie LLM+DB na turę, analiza dokumentu na 19 fragmentów robi ~20. Ten sam błąd
   „raz na X wywołań" trafia w analizę ~20× częściej.
5. **45% analiz w historii nie skończyło się statusem „Done"**, ale ta liczba jest myląca —
   sesje żyją WYŁĄCZNIE w pamięci procesu (świadoma decyzja prywatności: treść dokumentu nigdy
   nie trafia na dysk), więc każdy restart/deploy w trakcie długo trwającej analizy (tu: do 36
   minut) zabija ją bezpowrotnie, nieodróżnialnie od realnej awarii.

Skala danych: **20 analiz, 4 użytkowników, 6 tygodni** (vs. 191 rozmów czatu / 540 wiadomości u
tych samych 4 osób) — to środowisko wewnętrznych testerów, nie produkcja z realnym ruchem. Każdy
procent w tym dokumencie należy czytać z tym zastrzeżeniem; mechanizmy (kod) są jednak pewne
niezależnie od N.

## Rekonstrukcja: sesja `01a05ec7`, `regulamin.pdf`, 2026-09-01 21:01–21:37

**[FAKT]** Pełna treść tabeli `analysis_units` dla tej sesji (19 fragmentów zgłoszonych,
18 zapisanych):

| # | Nagłówek | Werdykt | Błąd |
|---|---|---|---|
| 1 | wstęp | BRAK ŹRÓDEŁ | — |
| 2 | § 1 | OK | — |
| 3 | § 2 | OK | — |
| 4 | § 3 | **?** (Unknown) | — (odpowiedź pusta, 0 znaków) |
| 5 | § 4 | OK | — |
| 6 | § 5 | BŁĄD | `LLM lokalny 500: [{"error":{"code":500,"message":"Internal error encountered.","status":"INTERNAL"}}]` |
| 7–12 | § 6…§ 11 | RYZYKO ×6 | — |
| *(brak)* | *§ 12* | **— brak wiersza w ogóle —** | *(zaginęło bezpowrotnie, zob. Problem 2)* |
| 14 | § 13 | BŁĄD | `An exception has been raised that is likely due to a transient failure.` (Npgsql/EF, patrz niżej) |
| 15 | § 14 | BŁĄD | `An error occurred while sending the request.` (HTTP do LLM) |
| 16 | § 15 | BRAK ŹRÓDEŁ | — |
| 17–19 | § 16…§ 18 | BŁĄD ×3 | `Wykorzystano limit planu Darmowy (15 zapytań...)` |

Zliczenie: 4× OK, 6× RYZYKO, 2× BRAK ŹRÓDEŁ, 5× BŁĄD, 1× „?", 1 fragment całkowicie zaginiony.
Status końcowy: **Done** (nie Failed/Interrupted — sesja „ukończyła się" w rozumieniu kodu, mimo
że jedna trzecia fragmentów nie ma użytecznego wyniku). Licznik zużycia planu dla tego
użytkownika w tym okresie rozliczeniowym: **15/15** — dokładnie limit darmowy, dokładnie w tej
sesji. Poniżej — dlaczego, fragment po fragmencie.

## Problem 1: limit planu liczony per jednostka, nie per dokument

**[FAKT]** `CostGuard.TryAcquireAsync` (jedno „zapytanie" z 15/300 miesięcznie) jest wołane w
`AnalysisRunner.AnalyzeUnitAsync` **raz na KAŻDY fragment** dokumentu
(`AnalysisRunner.cs:120`), a dodatkowo raz na streszczenie (`AnalysisRunner.cs:166`). Nie ma
osobnej puli dla analizy — `PlanLimits.RequestsPerMonth` to jedna wspólna liczba dla czatu i
analizy (`PlanOptions.cs:51`: *„Zapytania do LLM na okres rozliczeniowy (czat + analiza liczą się
wspólnie)"* — to jest udokumentowana decyzja projektowa, nie przeoczenie, ale konsekwencje nie
wyglądają na przemyślane pod kątem UX analizy).

Skutek: **dokument o N fragmentach kosztuje N+1 „zapytań"**, dokładnie tyle co N+1 osobnych
wiadomości na czacie. `MaxUnits=40` (limit fragmentów na dokument, `AnalysisSession.cs:19`)
oznacza, że jeden dokument może teoretycznie skonsumować 41 zapytań — **prawie trzykrotność**
całego miesięcznego limitu darmowego w jednym kliknięciu. Zmierzone na tej sesji: fragmenty 16–19
(§ 15…18) trafiły w ścianę limitu jeden po drugim, dokładnie tak jak zgłosił użytkownik.

Dodatkowy, mniej oczywisty efekt: przycisk **„↻ Ponów nieudane"** (`Analiza.razor:203`) woła
`AnalysisRunner.RetryUnitsAsync`, który przechodzi przez TĘ SAMĄ bramkę kosztów per jednostka
(`AnalysisRunner.cs:51-59` → `MapUnitsAsync`) — po wyczerpaniu limitu, kliknięcie „ponów" na
błędach 13/14/16-18 natychmiast odbije się o ten sam mur, bez żadnej wskazówki w UI, że to
oczekiwane (przycisk wygląda identycznie jak wtedy, gdy retry ma sens). Retry przelicza też
**streszczenie od nowa** (`AnalysisRunner.cs:48-50`, celowo — stare mogło opisywać błędy, których
już nie ma) — czyli konsumuje jeszcze jedno zapytanie, nawet jeśli ponawiana jest jedna jednostka.

### Rekomendacja użytkownika (osobna pula) — ocena

Propozycja z zgłoszenia (osobna pula: np. 3 analizy/mies. darmowy, 50/mies. płatny) jest
architektonicznie tania: `PlanLimits` to zwykły słownik konfiguracyjny
(`Plans:Items:{plan}:RequestsPerMonth`), dołożenie drugiej osi (`AnalysesPerMonth`) i drugiego
scope'u w `IUsageCounters` to rozszerzenie istniejącego wzorca, nie przebudowa.

Jedno zastrzeżenie, którego propozycja nie rozwiązuje sama z siebie: **jeśli pula liczy
"analizy" a nie "jednostki", trzeba PRZESTAĆ obciążać per fragment i zacząć obciążać per
dokument** (1 analiza = 1 jednostka zużycia puli, niezależnie od tego, czy dokument ma 3 czy 40
fragmentów) — inaczej „3 analizy/mies." w praktyce znaczy „od 3 do 120 wywołań LLM/mies." w
zależności od długości dokumentu, co niczego nie stabilizuje pod kątem kosztu infrastruktury.
Konsekwencja: potrzebny jest **osobny cap na `MaxUnits` per plan** (dziś 40 dla wszystkich) — bo
inaczej pula „analiz" i tak nie chroni serwera przed jednym bardzo długim dokumentem. Do
rozstrzygnięcia też: czy retry nieudanej jednostki po fakcie kosztuje kolejną „analizę" z puli,
czy jest wliczony w already-zapłaconą — dziś odpowiedź brzmi „tak, kosztuje", co przy obecnym
UX (patrz wyżej) będzie zaskakujące.

## Problem 2: cichy, trwały zanik jednostki (§ 12 zaginął)

**[FAKT — dowód przez eliminację]** `AnalysisId=01a05ec7` ma `UnitsTotal=19`, ale tylko 18
wierszy w `analysis_units` — brakuje dokładnie `UnitIndex=13` (nagłówek byłby „§ 12", licząc
z ciągu sąsiadów). Status analizy to **Done**, nie Interrupted/Failed.

Dlaczego to jest DOWÓD, nie zgadywanie: w `MapUnitsAsync` (`AnalysisRunner.cs:91-114`) każda
jednostka kończy się jedną z dwóch dróg — sukces (`result` z odpowiedzią) albo złapany wyjątek
(`catch (Exception ex) when (ex is not OperationCanceledException)` → `result` z
`UnitVerdict.Error`) — w OBU przypadkach `result` jest niepuste i leci do `UpsertUnitAsync`.
Jedyna droga, która NIE tworzy wiersza, to `OperationCanceledException` wymykająca się z zadania —
ale to zatrzymałoby CAŁY `Task.WhenAll` i poprzez `ExecuteAsync` ustawiło status na
**Interrupted**, nie Done. Skoro status to Done, ten fragment MUSIAŁ dostać wynik w pamięci
(`session.SetUnitResult(result)` poszło, stąd live-view podczas sesji prawdopodobnie POKAZYWAŁ
werdykt), a jedyne miejsce, które może zgubić już-gotowy wynik bez wywrócenia całości, to zapis:

```csharp
// AnalysisRunner.cs:187-191
private static async Task Persist(Func<Task> op)
{
    try { await op(); } catch { /* best-effort */ }
}
```

`UpsertUnitAsync` rzucił, wyjątek został połknięty w całości — **bez logowania**. `AnalysisRunner`
nie ma wstrzykniętego `ILogger` w ogóle (konstruktor: `IServiceScopeFactory, IOptions<AnalysisOptions>,
CostGuard, IAnalysisStore` — cztery zależności, zero telemetrii). To jest zgodne z udokumentowaną
intencją projektową („Persystencja raportu w całości BEST-EFFORT — analiza dla użytkownika ma
priorytet nad zapisem", `AnalysisRunner.cs:16-17`) — dobra zasada dla NIE WYWRACANIA analizy przy
awarii zapisu, ale zrealizowana bez rozróżnienia „nic się nie stało" od „user właśnie stracił
kawałek raportu na zawsze". Odzyskanie niemożliwe: treść dokumentu istnieje tylko w
`AnalysisSessionStore` (in-memory, TTL 60 min), po odświeżeniu/wygaśnięciu sesji raport ładowany
jest z DB w trybie „zdegradowanym" (`_degraded=true`, `Analiza.razor:166-168`), gdzie retry per
jednostka jest wyłączony (`!_degraded` na przycisku `↻ ponów`, `Analiza.razor:249`) — jedyna droga
odzyskania to nowa analiza od zera, z ponownym zużyciem limitu z Problemu 1.

To jedyny znaleziony fragment brakujący na 148 jednostek w całej historii tabeli, więc **[HIPOTEZA]**
to rzadkie zdarzenie, prawdopodobnie skorelowane z Problemem 4 (ta sama sesja ma DWA sąsiadujące
błędy transportowe kilka fragmentów dalej — patrz niżej) — czyli mogła to być ta sama chwila
niestabilności hosta, tyle że tym razem trafiła w zapis zamiast w odczyt/generację.

**Rekomendacja**: minimum — dodać `ILogger` do `AnalysisRunner`/`AnalysisStore` i logować (nie
rzucać dalej) każdy połknięty wyjątek w `Persist`, z ID analizy i jednostki. Do rozważenia: jeden
ponowny odczyt/retry samego zapisu (bez ponownego wywołania LLM — wynik już jest w pamięci) przed
poddaniem się — dziś nawet jednorazowy transient DB error kasuje wynik bezpowrotnie, mimo że
koszt naprawy (retry samego INSERT-u) jest zerowy w porównaniu do kosztu utraty (całe wywołanie
LLM + limit z puli, żeby to odtworzyć).

## Problem 3: werdykt „?" — puste odpowiedzi lokalnego LLM, nie błąd parsowania

**[FAKT]** `AnalysisPrompts.ParseVerdict` zwraca `UnitVerdict.Unknown` (etykieta „?" w UI,
`AnalysisPrompts.cs:64`), gdy pierwsza linia odpowiedzi nie zaczyna się od `WERDYKT:`. Sprawdzone
hipotezy dlaczego:

- **Odrzucone**: parsowanie po stronie zapisu (`AnalysisStore.ParseVerdict`, czytanie z DB) — w
  całej tabeli `analysis_units` występuje wyłącznie 5 poprawnych nazw enuma (`Error, Unknown, Risk,
  Ok, NoSources`), więc odczyt z bazy nikogo nie psuje.
- **Odrzucone (potwierdzone jako historyczny, już naprawiony artefakt)**: wyciek surowego
  „myślenia" modelu (`<thought>...</thought>`) do widocznej odpowiedzi. Znaleziono 7 jednostek z
  Unknown i odpowiedzią 4–17 KB czystego angielskiego chain-of-thought — ale WSZYSTKIE pochodzą z
  **jednej analizy z 2026-07-22 20:59**, czyli sprzed wdrożenia `ReasoningSplitter`
  (`de4c87e`, 2026-07-23) — dosłownie dzień przed naprawą. Sprawdzone: KAŻDA jednostka z werdyktem
  Unknown w analizach PO tej dacie (6 analiz, 07-23 do 09-01, w tym `regulamin.pdf` użytkownika)
  ma `Answer` długości **dokładnie 0**.
- **Potwierdzone jako aktualny mechanizm**: model zwraca pustą treść (0 znaków) dla tego
  fragmentu. `ParseVerdict` na pustym stringu trafia w gałąź „pierwsza linia nie zaczyna się od
  WERDYKT:" i zwraca `Unknown` z pustym `Answer` — to dokładnie to, co widać w danych i dokładnie
  to zobaczył użytkownik (badge „?" bez treści pod spodem).

**[DOMYSŁ — wymaga repro]** Dlaczego lokalny model czasem zwraca zero tokenów dla konkretnego
fragmentu, nie mam jak stąd zdiagnozować (log serwera LLM poza zasięgiem tego repo) — może to być
natychmiastowy EOS na specyficznym wejściu, kwestia stosu serwującego, albo interakcja z
`ReasoningEffort`. Dobra wiadomość: `OpenAiCompatibleLlmProvider` ma już wbudowany hak
diagnostyczny na dokładnie ten przypadek — `PRAWORAG_DUMP_RESPONSE=<ścieżka>` zrzuca surowe linie
SSE `data:` z odpowiedzi (`OpenAiCompatibleLlmProvider.cs:114-137`). Najtańszy następny krok:
włączyć to na środowisku, poczekać na kolejne „?", i zobaczyć, czy strumień faktycznie jest pusty
od serwera, czy coś obcina go po drodze.

**Rekomendacja**: niezależnie od przyczyny, dzisiejsze zachowanie (pusta odpowiedź → trwały
werdykt „?" bez żadnej automatycznej reakcji) jest gorsze niż potrzeba — pusta odpowiedź z LLM to
dokładnie ten rodzaj błędu, który jeden automatyczny retry (bez zużywania dodatkowego limitu z
puli, bo to naprawa własnego błędu systemu, nie nowe zapytanie usera) prawdopodobnie by naprawił.

## Problem 4: zero odporności na przejściowe zrywy — ale ekspozycja analizy jest ~20× wyższa

**[FAKT]** Ani w `OpenAiCompatibleLlmProvider`, ani w `ChatService`, ani w warstwie DB (brak
`EnableRetryOnFailure` na `UseNpgsql`, `StorageServiceCollectionExtensions.cs:17`) nie ma ŻADNEGO
mechanizmu retry/backoff na błędy przejściowe — potwierdzone grepem (`Polly|Resilience|Retry|
CircuitBreaker` → zero trafień w `Llm`/`Api`). Jeden nieudany request = jedna stracona jednostka,
zawsze, w obu ścieżkach.

Dwa różne błędy z tej samej sesji, oba na sąsiadujących fragmentach (§ 13, § 14), mają dwa różne
źródła:

- `An exception has been raised that is likely due to a transient failure.` — to standardowy
  komunikat Entity Framework Core, opakowujący błąd Npgsql przy nieudanym połączeniu z bazą
  (potwierdzony dokładnie ten tekst w `src/PrawoRAG.Ingestion/logs/process-failures-*.jsonl`, tam
  z pełnym stack trace: `Npgsql.NpgsqlException: Failed to connect to 192.168.100.11:5432` /
  `TimeoutException: Timeout during connection attempt`) — czyli retrieval (baza) dla tego
  fragmentu nie mógł się połączyć z Postgresem.
- `An error occurred while sending the request.` — standardowy `.Message` dla `HttpRequestException`
  przy nieudanym połączeniu HTTP — w tym kontekście do lokalnego serwera LLM.

**[HIPOTEZA]** Baza danych i (najpewniej) lokalny LLM siedzą na tym samym hoście
(`192.168.100.11` — ten sam adres co connection string bazy). Dwa różne typy błędu
połączenia na dwóch kolejnych fragmentach, kilka minut od siebie, silnie sugerują **jeden wspólny
epizod niestabilności hosta/sieci** tego wieczoru, a nie dwa niezależne bugi aplikacyjne. To nie
zwalnia aplikacji z potrzeby retry (patrz niżej) — ale zmienia gdzie szukać pierwotnej przyczyny
(infrastruktura współdzielona przez DB+LLM, nie osobno „LLM jest zawodny" i „baza jest zawodna").

Kluczowy punkt do zapamiętania: **czat ma dokładnie ten sam brak odporności i dokładnie ten sam
wzorzec „pokaż surowy `ex.Message` userowi"** — `Chat.razor:814` (`catch (Exception e) { ex.Error =
e.Message; }`) i endpoint `/api/chat` (`Program.cs:699-703`) robią to samo, co
`AnalysisRunner.cs:102-106`. `ChatErrorEvent` jest zdefiniowany i konsumowany w 3 miejscach, ale
**nigdy nie jest jawnie tworzony** (`new ChatErrorEvent(...)` nie występuje nigdzie w kodzie) —
błędy zawsze docierają jako gołe wyjątki, łapane na najbliższym try/catch, w obu ścieżkach
identycznie. Różnica w tym, jak awaryjnie czuje się analiza vs. czat, to **matematyka
ekspozycji**, nie różnica jakości kodu: jedna wiadomość czatu = 1 wywołanie LLM (+ retrieval), 19-
fragmentowy dokument = ~19-krotność tego ryzyka w jednym uruchomieniu. Ten sam „1 błąd na 100
wywołań" wygląda jak „prawie nigdy" na czacie i jak „prawie zawsze coś się posypie" na dłuższym
dokumencie — czysta arytmetyka, potwierdzona przez to, że TA SAMA sesja miała 3 różne rodzaje
błędów transportowych na 19 wywołaniach.

**Rekomendacja**: jeden ograniczony retry (1 próba, krótki backoff) na poziomie pojedynczego
wywołania LLM i pojedynczego zapytania retrievalu prawdopodobnie eliminuje większość tej klasy
błędów — to nie musi być pełny Polly/circuit-breaker, wystarczy pętla `for (attempt in 1..2)`
wokół `StreamCompletionAsync`/zapytania do bazy w newralgicznych miejscach. Warto to zrobić w
warstwie WSPÓLNEJ (`ChatService`/`OpenAiCompatibleLlmProvider`), żeby czat też skorzystał, zamiast
łatać tylko `AnalysisRunner`.

## Problem 5: sesje wyłącznie w pamięci + długi czas trwania + częste deploye → „Interrupted" nic nie mówi na pewno

**[FAKT]** 9 z 20 analiz w historii (45%) ma status `Interrupted`, ale to liczba z **trzema
nierozróżnionymi przyczynami** stopionymi w jeden status: (a) `MarkAllInterruptedAsync` zamiata
KAŻDY rekord w stanie `Analyzing` na starcie procesu (`AnalysisStore.cs:145-153`,
`AnalysisRunner.cs` docstring: „restart = sesje in-memory zginęły" — świadoma decyzja prywatności,
treść dokumentu nigdy nie dotyka dysku), (b) jawne anulowanie przez użytkownika
(`AnalysisSessionStore.Remove`, przycisk „⏹ Anuluj"), (c) wygaśnięcie TTL sesji (60 min) w trakcie
bezczynności. Dane pokazują konkretne przypadki niemożliwe do wyjaśnienia jako „analiza się nie
udała": `019f94d4` — utworzona 2026-07-24 15:53, zaktualizowana **2026-08-11** (18 dni później,
oczywisty sweep przy jakimś późniejszym restarcie, nie żywa 18-dniowa analiza); `01a03408` —
utworzona 08-24 13:49, zaktualizowana 08-25 18:58 (podobnie). Z drugiej strony `019f8e48-09e9`
(0 jednostek zapisanych, 4 sekundy między create/update) wygląda na realną, szybką awarię
startową.

To NIE jest fałszywy alarm co do samego mechanizmu — analiza `regulamin.pdf` trwała **36 minut**
(21:01→21:37) na 19 fragmentach przy `MaxParallelism=2`; przy takim czasie trwania
prawdopodobieństwo, że deploy (a repo ma historię wielu commitów dziennie) trafi w środek żywej
sesji, jest realna i strukturalna, nie przypadkowa. To świadomy kompromis prywatność-vs-ciągłość
(dokument nigdy nie trafia na dysk = zero ryzyka wycieku, ale też zero przetrwania restartu) —
**ale dziś nie da się odróżnić „padło, bo deploy" od „padło, bo błąd"**, bo oba lądują w tym samym
statusie bez żadnego pola przyczyny.

**Rekomendacja**: dodać pole `InterruptReason` (`ProcessRestart` / `UserCancelled` /
`TtlExpired`) przy zapisie statusu Interrupted — to nie zmienia zachowania, tylko czyni tę liczbę
mierzalną zamiast domyślaną. Bez tego każda przyszła dyskusja o „ile % analiz faktycznie się
wywala" będzie musiała powtórzyć tę samą ręczną archeologię SQL.

## Co NIE jest zepsute (żeby nie przeceniać obrazu)

- **Grounding/jakość retrievalu jest DZIEDZICZONA z czatu i dojrzała.** `AnalyzeUnitAsync` woła
  `IChatService.AskAsync` z `forceRetrieval: true` (`AnalysisRunner.cs:138-139`) — czyli
  korzysta z tego samego, na bieżąco poprawianego rdzenia: bramki abstynencji, druga runda
  retrievalu (`retrying_retrieval`), `AnswerGate`/regeneracja, most cytowań przepisów. Wszystkie
  niedawne naprawy jakości retrievalu (m.in. próg gap-closing, filtr `AbsorbedAmendment`, chunking
  na poziomie ustępu) automatycznie obejmują też analizę dokumentów, bo to ten sam kod. Ten
  dokument NIE znalazł żadnego problemu jakości groundingu specyficznego dla `/analiza` — to, co
  jest zepsute, to warstwa WOKÓŁ tego rdzenia (limity, zapis, obsługa błędów transportu), nie sam
  rdzeń.
- **BRAK ŹRÓDEŁ to w dużej mierze oczekiwane zachowanie, nie defekt.** 37/148 jednostek w historii
  (25%) — komentarz w kodzie (`AnalysisRunner.cs:133-137`) wprost tłumaczy dlaczego: preambuła,
  komparycja, dane stron w umowie często nie zawierają żadnego twierdzenia prawnego do
  zweryfikowania, więc poprawna odpowiedź to odmowa, nie wymyślona ocena. Poprzedni raport
  (07-23) rozbił to dokładniej na kategorię A (fragment strukturalnie bez treści prawnej) i
  kategorię B (dokument odwołuje się do aktu prawa miejscowego poza zakresem korpusu) — obie
  „poprawne odmowy", nie awarie.
- **Raw-error-message UX to problem WSPÓLNY z czatem, nie luka analizy specyficznie** — patrz
  Problem 4. Warto to naprawić w jednym miejscu (warstwa `ChatService`/LLM provider), nie
  osobno dla `/analiza`.

## Rekomendacje — priorytety

**P0 (tania, duży wpływ na odczuwaną awaryjność):**
1. Osobna pula limitu dla analiz, naliczana **per dokument, nie per fragment** + osobny (niższy)
   cap `MaxUnits` per plan darmowy, żeby pula faktycznie ograniczała koszt infrastruktury
   (Problem 1).
2. Logowanie połkniętych wyjątków w `Persist`/`AnalysisStore` — dziś zero telemetrii na jedyny
   znaleziony przypadek trwałej utraty danych (Problem 2).
3. Jeden ograniczony retry na wywołanie LLM (wspólnie dla czatu i analizy) — prawdopodobnie
   eliminuje większość błędów transportowych widocznych w tej sesji (Problem 4).

**P1 (średni koszt, porządkuje diagnostykę na przyszłość):**
4. Automatyczny pojedynczy retry jednostki, gdy `ParseVerdict` dostanie pustą/niesparsowalną
   odpowiedź, zanim werdykt trwale wyląduje jako „?" (Problem 3) — plus użycie
   `PRAWORAG_DUMP_RESPONSE` na żywym środowisku, żeby złapać repro i zrozumieć przyczynę źródłową.
   [DOMYSŁ — wymaga repro].
5. Pole `InterruptReason` na rekordzie analizy, żeby „Interrupted" przestało być czarną skrzynką
   (Problem 5).

**P2 (do rozważenia, nie pilne):**
6. UX przycisku „↻ Ponów nieudane" po wyczerpanym limicie — dziś klika się w mur bez ostrzeżenia;
   albo wyłączyć przycisk przy `PlanLimit`, albo pokazać ten sam komunikat od razu bez kolejnego
   roundtripu.
7. Retry pojedynczej jednostki po limicie/błędzie transportowym mógłby NIE liczyć się do puli
   ponownie (to naprawa błędu systemu, nie nowe zapytanie użytkownika) — do decyzji razem z P0.1.

## Otwarte pytania / czego nie sprawdzono stąd

- Czy `192.168.100.11` rzeczywiście hostuje i bazę, i lokalny LLM na jednym fizycznym/wirtualnym
  hoście — to by wyjaśniało współwystępowanie błędów DB+HTTP w tej samej sesji, ale nie mam stąd
  wglądu w topologię infrastruktury poza connection stringami w repo.
- Realna przyczyna pustych odpowiedzi LLM (Problem 3) wymaga `PRAWORAG_DUMP_RESPONSE` na żywym
  środowisku przy kolejnym wystąpieniu — nie da się jej dalej zawęzić z samego kodu i danych DB.
- N=20 analiz / 4 testerów to zbyt mało, żeby traktować jakikolwiek procent w tym dokumencie jako
  stabilny szacunek częstości w warunkach realnego ruchu — mechanizmy (kod) są pewne, częstości
  nie.
