# Odsiew szumu w retrievalu (R1–R4) — plan implementacji

> **Dla agentów:** WYMAGANY SUB-SKILL: `superpowers:subagent-driven-development` (zalecany) albo
> `superpowers:executing-plans`. Kroki mają checkboxy (`- [ ]`) do śledzenia postępu.

**Cel:** przenieść istniejący sygnał cross-encodera tam, gdzie zapadają decyzje o źródłach (wybór
wariantu follow-upu, wpuszczanie mostu cytowań), i skasować trzy rozjechane kopie logiki follow-upu —
bez dodawania nowego kroku pipeline'u i bez mapowań słów kluczowych.

**Architektura:** trzy zmiany, wszystkie na istniejących klockach. (1) `RetrievalQuery` dostaje
`RerankText` — dokładnie ten sam wzorzec co istniejące `ExactMatchText` — dzięki czemu reranker ocenia
kandydatów względem SUROWEGO pytania użytkownika, nie względem sklejki, która ocenia samą siebie.
(2) Wybór wariantu follow-upu przenosi się na `RerankTopScore` i ląduje w JEDNYM miejscu
(`FollowUpSelector`), z którego korzystają wszystkie trzy ścieżki. (3) Most cytowań przestaje mieć
gwarantowany slot i przestaje przyjmować głosy od kandydatów, których reranker uznał za nieistotne.

**Stack:** .NET 10, EF Core + Npgsql/pgvector, xUnit (`[Collection("LiveDb")]` = testy na żywym
Postgresie), TEI cross-encoder `sdadas/polish-reranker-roberta-v3` na `:8081`.

## Diagnoza, z której wynika ten plan

Pomiary z 2026-08-11 (korpus ELI 14 341 / NSA 10 738 / SAOS 508 752, model `gemma-4-26b-a4b-it`):

| wariant zapytania follow-upu | max cosine | sloty z uodo (DU/2018/1000) |
|---|---|---|
| samo pytanie 2 (tak liczy je czat jako „surowy") | 0.8431 | **5/8** |
| rdzeń: pytanie 1 + pytanie 2, bez foldu | 0.8158 | 4/8 |
| pełny `Contextualize` (rdzeń + kotwice + 400 zn. odpowiedzi) | **0.8576** | **0/8** |
| to samo BEZ kotwic (rdzeń + fragment odpowiedzi) | 0.8378 | art. 107 na **#2** |

Fold PODNOSI cosine i jednocześnie pogarsza trafność — `PickContextual` wybiera więc gorszy wariant.
**Winne są KOTWICE, nie fragment odpowiedzi.** Ostatni wiersz to pomiar rozstrzygający: wystarczy
usunąć ze sklejki kotwice źródeł, a wraca uodo art. 107 — przepis, który dla samego pytania 1 nie
mieścił się w top-50. Kotwica niesie numer Dziennika Ustaw i numer artykułu poprzedniej tury
(`…, art. 37, Dz.U. 2011 nr 113 poz. 657, DU/2011/657`), więc (a) w każdej ścieżce bez
`ExactMatchText` wyzwala tory DOKŁADNE — zmierzone: wszystkie 8 slotów `/api/search` ze
`Score = double.MaxValue`, cały budżet zjedzony przez akt z poprzedniej tury — i (b) nawet tam, gdzie
tory dokładne są odcięte (czat ustawia `ExactMatchText`), dominuje BM25 tytułem i numerem aktu.

Ten sam reranker, na tych samych pasażach, względem surowego pytania 2 rozdziela bez pudła:

| pasaż | score cross-encodera |
|---|---|
| uodo art. 60 (zgłoszenie naruszenia Prezesowi UODO) | **0.8842** |
| uodo art. 107 | 0.2967 |
| szum SAOS („pomawiający") | 0.1733 |
| ustawa o systemie informacji w ochronie zdrowia, art. 37 | 0.0503 |
| tejże art. 2 (definicje — to one wygrały w foldzie) | **0.0009** |

**Dwie korekty wcześniejszych diagnoz — obie robione zbyt szybko, obie odwołane pomiarem:**

1. Wersja robocza tłumaczyła wstrzyknięcie KK art. 64 § 1-3 tym, że II AKa 78/18 to apelacja od
   III K 93/17, więc „ta sama sprawa głosowała dwa razy". Nieuzasadnione — apelacja ma własną
   sygnaturę, więc grupowanie po `CaseNumber` i tak by ich nie skleiło, a `BridgeMinDocVotes = 2`
   był spełniony legalnie.
2. Wersja robocza przypisywała wstrzyknięcie KK art. 64 MOSTOWI CYTOWAŃ. Sprawdzone: w sondzie
   sklejki KK art. 64 nie ma w ogóle, a wszystkie sloty ze `Score = double.MaxValue` należą do aktu
   z kotwic, czyli do torów DOKŁADNYCH. **Skąd KK art. 64 § 1-3 wziął się w czacie — NADAL NIE
   WIADOMO** i żadne zadanie w tym planie nie twierdzi, że to naprawia. Reprodukcja: `POST /api/chat`
   z historią złożoną z pytania 1, jego odpowiedzi i czterech kotwic źródeł tamtej tury; KK art. 64
   § 1/2/3 wchodzi na sloty [1][2][3]. To osobny wątek diagnostyczny, nie pozycja tego planu.

Wada, która UZASADNIA Zadanie 4, jest ogólna i widoczna w kodzie bez tego przypadku: **głosować mogą
WSZYSCY kandydaci po fuzji RRF**, także ci, których cross-encoder ocenia na 0,17 (zmierzone wartości
dla puli z pytania 1 — tabela wyżej), a zwycięzca głosowania dostaje slot z pominięciem rerankera
(`HybridRetriever.cs:214`, `exact.Concat(bridge).Concat(ranked)`). Zadanie 4 zamyka tę dziurę
prewencyjnie, nie jako fix zaobserwowanego objawu.

**Znalezisko blokujące pomiary:** logika follow-upu istnieje w TRZECH kopiach i już się rozjechała —
`RefusalEvalRunner.cs:161` woła starą przeciążkę `Contextualize(questions, question)`: bez foldu i bez
`ExactMatchText`, mimo własnego komentarza „rozjazd z ChatService = rozjazd metryki". Harness `--refusals`
NIE odtwarza dziś błędu, który zmierzyliśmy w produkcji. Dlatego scalenie kopii (Zadanie 3) musi
wyprzedzić jakąkolwiek kalibrację (Zadanie 6).

## Global Constraints

- Docelowa gałąź: bieżąca `feat/halfvec-retriever`. Nie merge'ować do `main` w ramach tego planu.
- **Zero nowych kroków pipeline'u i zero mapowań słowo→źródło.** Dozwolone: przeniesienie istniejącego
  sygnału, scalenie duplikatów, usunięcie kodu. To wymaganie właściciela produktu, nie preferencja stylu.
- **Prawo UE (RODO) NIE wchodzi w tym etapie.** Pytanie 1 pozostaje odpowiadalne tylko częściowo i to
  jest stan oczekiwany — nie „naprawiać" go obchodzeniem retrievalu.
- `Reranker:Enabled` jest domyślnie `false` w `src/PrawoRAG.Api/appsettings.json`. Przy wyłączonym
  rerankerze `RetrievalResult.RerankTopScore` jest `null` na obu wariantach, więc **cały R1 degraduje
  się do dzisiejszego zachowania razem z dzisiejszym bugiem**. Każdy bieg weryfikacyjny i evalowy z
  tego planu MUSI iść z `Reranker__Enabled=true` i żywym TEI na `:8081`.
- Testy `[Collection("LiveDb")]` wymagają Postgresa pod `PRAWORAG_DB` (domyślnie
  `Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag`).
- Komentarze i nazwy testów po polsku, zgodnie z resztą repo. Komentarz tłumaczy DLACZEGO, nie CO.
- Commity po każdym zadaniu, prefiksy jak w historii: `feat(retrieval):`, `fix(retrieval):`,
  `refactor(retrieval):`, `test(retrieval):`, `docs:`.

## Struktura plików

| Plik | Odpowiedzialność | Zadanie |
|---|---|---|
| `src/PrawoRAG.Domain/Retrieval/Retrieval.cs` | `RetrievalQuery.RerankText` + `EffectiveRerankText` | 1 |
| `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs` | reranker czyta `EffectiveRerankText`; most za bramką | 1, 4 |
| `src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs` | czysta decyzja: przeciążka `PickContextual` na wynikach | 2 |
| `src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs` (NOWY) | jedyna orkiestracja podwójnego retrievalu | 3 |
| `src/PrawoRAG.Api/Services/ChatService.cs` | woła `FollowUpSelector` | 3 |
| `src/PrawoRAG.Api/Program.cs` | woła `FollowUpSelector`; `RetrievalOptions.RerankSignalMargin` | 3 |
| `src/PrawoRAG.Eval/RefusalEvalRunner.cs` | woła `FollowUpSelector` (koniec rozjazdu metryki) | 3 |
| `src/PrawoRAG.Eval/golden-set.json` | dwie nowe pozycje pomiarowe | 5 |
| `tests/PrawoRAG.Tests/Fakes/FakeReranker.cs` | zapamiętuje `LastQuery` | 1 |
| `tests/PrawoRAG.Tests/Fakes/FakeRetriever.cs` (NOWY) | deterministyczny `IRetriever` do testów selektora | 3 |
| `tests/PrawoRAG.Tests/Retrieval/*` | testy jednostkowe + LiveDb | 1–4 |

---

### Zadanie 1: `RerankText` — reranker ocenia względem pytania, nie sklejki

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Retrieval/Retrieval.cs:20-24` (obok `ExactMatchText`)
- Modify: `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs:169`
- Modify: `tests/PrawoRAG.Tests/Fakes/FakeReranker.cs`
- Test: `tests/PrawoRAG.Tests/Retrieval/HybridRetrieverTests.cs` (dopisz na końcu klasy)

**Interfejsy:**
- Produces: `RetrievalQuery.RerankText { get; init; }` (`string?`), `RetrievalQuery.EffectiveRerankText`
  (`string`, nigdy null), `FakeReranker.LastQuery` (`string?`). Zadanie 3 ustawia `RerankText`.

- [ ] **Krok 1: Napisz test, który ma paść**

W `tests/PrawoRAG.Tests/Retrieval/HybridRetrieverTests.cs`, przed zamykającą klamrą klasy:

```csharp
    [Fact] // R7: reranker dostaje RerankText (surowe pytanie), nie Text (sklejkę follow-upu)
    public async Task Reranker_scores_against_rerank_text_not_query_text()
    {
        const string src = "TEST-RETR-7";
        await CleanAsync(src);
        await SeedAsync(src, "a", DocTypes.Judgment, "Reranktekst alfa przepis testowy pierwszy", tokenCount: 20);

        await using var db = NewDb();
        var reranker = new FakeReranker("alfa");
        await new HybridRetriever(db, Emb, reranker).RetrieveAsync(
            new RetrievalQuery
            {
                Text = "Reranktekst przepis testowy SKLEJKA z poprzedniej odpowiedzi",
                RerankText = "Reranktekst surowe pytanie użytkownika",
                MinChunkTokens = 0,
            }, default);

        // Sedno: sklejka nie może oceniać samej siebie — cross-encoder sądzi po pytaniu użytkownika.
        Assert.Equal("Reranktekst surowe pytanie użytkownika", reranker.LastQuery);
        await CleanAsync(src);
    }

    [Fact] // R8: bez RerankText reranker dostaje Text (zgodność wsteczna — /api/search, pytania bez historii)
    public async Task Reranker_falls_back_to_query_text()
    {
        const string src = "TEST-RETR-8";
        await CleanAsync(src);
        await SeedAsync(src, "a", DocTypes.Judgment, "Rerankfallback alfa przepis testowy", tokenCount: 20);

        await using var db = NewDb();
        var reranker = new FakeReranker("alfa");
        await new HybridRetriever(db, Emb, reranker).RetrieveAsync(
            new RetrievalQuery { Text = "Rerankfallback przepis testowy", MinChunkTokens = 0 }, default);

        Assert.Equal("Rerankfallback przepis testowy", reranker.LastQuery);
        await CleanAsync(src);
    }
```

- [ ] **Krok 2: Uruchom test i potwierdź, że nie kompiluje**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~Reranker_scores_against_rerank_text"
```
Oczekiwane: błąd kompilacji — `RetrievalQuery` nie ma `RerankText`, `FakeReranker` nie ma `LastQuery`.

- [ ] **Krok 3: Dodaj `LastQuery` do `FakeReranker`**

W `tests/PrawoRAG.Tests/Fakes/FakeReranker.cs`, w ciele klasy obok `Calls`:

```csharp
    /// <summary>Ostatnie zapytanie przekazane cross-encoderowi — pozwala sprawdzić, że retriever
    /// ocenia kandydatów względem pytania użytkownika, a nie względem sklejki follow-upu.</summary>
    public string? LastQuery { get; private set; }
```

i w pierwszej linii `RerankAsync`, przed `Calls++`:

```csharp
        LastQuery = query;
```

- [ ] **Krok 4: Dodaj `RerankText` do `RetrievalQuery`**

W `src/PrawoRAG.Domain/Retrieval/Retrieval.cs`, bezpośrednio pod `EffectiveExactMatchText` (linia 24):

```csharp
    /// <summary>
    /// Tekst, którym cross-encoder ocenia kandydatów — gdy różni się od <see cref="Text"/>. Null =
    /// użyj <see cref="Text"/> (domyślne: /api/search, pytania bez historii, testy). Rozdzielenie
    /// istnieje z tego samego powodu co <see cref="ExactMatchText"/>, tylko po stronie SĘDZIEGO:
    /// przy follow-upie <see cref="Text"/> niesie fold z POPRZEDNIEJ ODPOWIEDZI, więc reranker
    /// dostawał do oceny tekst, którego spory kawałek sam był ocenianą treścią — sklejka oceniała
    /// samą siebie i wygrywała mimo gorszych źródeł (zmierzone 2026-08-11: fold 0.8576 cosine przy
    /// 0/8 trafnych slotów vs surowe 0.8431 przy 5/8). Tor gęsty/BM25 dalej czyta pełny
    /// <see cref="Text"/> — wzbogacenie semantyczne pod anaforę zostaje nietknięte.
    /// </summary>
    public string? RerankText { get; init; }

    /// <summary>Tekst faktycznie zasilający cross-encoder: <see cref="RerankText"/> jeśli podany,
    /// inaczej <see cref="Text"/>.</summary>
    public string EffectiveRerankText => RerankText ?? Text;
```

- [ ] **Krok 5: Podepnij w `HybridRetriever`**

W `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs:169` zamień:

```csharp
            var scores = await reranker.RerankAsync(query.Text, deduped.Select(c => c.Text).ToList(), ct);
```

na:

```csharp
            var scores = await reranker.RerankAsync(query.EffectiveRerankText, deduped.Select(c => c.Text).ToList(), ct);
```

- [ ] **Krok 6: Uruchom testy i potwierdź, że przechodzą**

```bash
docker start praworag-db-1   # albo `cd infra && podman compose up -d db`
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~HybridRetrieverTests"
```
Oczekiwane: PASS, wszystkie testy klasy (R1–R8) zielone.

- [ ] **Krok 7: Commit**

```bash
git add src/PrawoRAG.Domain/Retrieval/Retrieval.cs \
        src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs \
        tests/PrawoRAG.Tests/Fakes/FakeReranker.cs \
        tests/PrawoRAG.Tests/Retrieval/HybridRetrieverTests.cs
git commit -m "feat(retrieval): reranker ocenia wzgledem RerankText, nie sklejki follow-upu"
```

---

### Zadanie 2: decyzja follow-upu na sygnale rerankera (czysta funkcja)

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs` (dopisz na końcu klasy, po `PickContextual`)
- Test: `tests/PrawoRAG.Tests/Retrieval/FollowUpQueryTests.cs` (dopisz na końcu klasy)

**Interfejsy:**
- Consumes: `RetrievalResult` z Zadania 0 (istniejący typ: `Chunks`, `MaxSimilarity`, `RerankTopScore`).
- Produces: `FollowUpQuery.DefaultRerankSignalMargin` (`const double` = 0.05),
  `FollowUpQuery.PickContextual(RetrievalResult raw, RetrievalResult contextual, double cosineMargin, double rerankMargin)`
  → `bool`. Zadanie 3 woła wyłącznie tę przeciążkę.

- [ ] **Krok 1: Napisz testy, które mają paść**

W `tests/PrawoRAG.Tests/Retrieval/FollowUpQueryTests.cs`, przed zamykającą klamrą klasy:

```csharp
    // --- PickContextual na WYNIKACH: gdy jest cross-encoder, decyduje on, nie cosine ---

    private static RetrievalResult Res(double cosine, double? rerank = null) =>
        new([], cosine, rerank);

    [Fact] // Zmierzone 2026-08-11: fold ma WYŻSZY cosine (0.8576 vs 0.8431) i ZERO trafnych źródeł.
    public void Rerank_signal_overrides_misleading_cosine()
    {
        var raw = Res(cosine: 0.8431, rerank: 0.8842);   // uodo art. 60 na wierzchu
        var ctx = Res(cosine: 0.8576, rerank: 0.0503);   // definicje z ustawy o systemie informacji
        Assert.False(FollowUpQuery.PickContextual(raw, ctx,
            cosineMargin: FollowUpQuery.DefaultSignalMargin,
            rerankMargin: FollowUpQuery.DefaultRerankSignalMargin));
    }

    [Fact] // Anafora („a co z § 2?") — wariant kontekstowy MUSI dalej wygrywać, gdy realnie trafia lepiej.
    public void Anaphoric_followup_still_picks_contextual_on_rerank()
    {
        var raw = Res(cosine: 0.879, rerank: 0.12);      // przypadkowe fragmenty
        var ctx = Res(cosine: 0.879, rerank: 0.55);      // właściwy artykuł z poprzedniej tury
        Assert.True(FollowUpQuery.PickContextual(raw, ctx,
            cosineMargin: FollowUpQuery.DefaultSignalMargin,
            rerankMargin: FollowUpQuery.DefaultRerankSignalMargin));
    }

    [Fact] // Asymetria zostaje na skali rerankera: surowy musi pobić kontekstowy o margines.
    public void Raw_within_rerank_margin_still_loses()
    {
        var raw = Res(cosine: 0.80, rerank: 0.60);
        var ctx = Res(cosine: 0.80, rerank: 0.58);       // różnica 0.02 < margines 0.05
        Assert.True(FollowUpQuery.PickContextual(raw, ctx,
            cosineMargin: FollowUpQuery.DefaultSignalMargin,
            rerankMargin: FollowUpQuery.DefaultRerankSignalMargin));
    }

    [Fact] // Reranker wyłączony (Reranker:Enabled=false) → oba RerankTopScore null → spadamy na cosine.
    public void Without_rerank_signal_falls_back_to_cosine()
    {
        var raw = Res(cosine: 0.85);
        var ctx = Res(cosine: 0.60);
        Assert.False(FollowUpQuery.PickContextual(raw, ctx,
            cosineMargin: FollowUpQuery.DefaultSignalMargin,
            rerankMargin: FollowUpQuery.DefaultRerankSignalMargin));
    }

    [Fact] // Sygnał rerankera TYLKO na jednym wariancie = nieporównywalny → cosine, nie zgadywanie.
    public void One_sided_rerank_signal_falls_back_to_cosine()
    {
        var raw = Res(cosine: 0.85, rerank: 0.99);
        var ctx = Res(cosine: 0.60);
        Assert.False(FollowUpQuery.PickContextual(raw, ctx,
            cosineMargin: FollowUpQuery.DefaultSignalMargin,
            rerankMargin: FollowUpQuery.DefaultRerankSignalMargin));
    }
```

- [ ] **Krok 2: Uruchom testy i potwierdź, że nie kompilują**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~FollowUpQueryTests"
```
Oczekiwane: błąd kompilacji — brak przeciążki `PickContextual(RetrievalResult, RetrievalResult, double, double)`
i stałej `DefaultRerankSignalMargin`.

- [ ] **Krok 3: Dopisz stałą i przeciążkę**

W `src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs`, na końcu klasy (po istniejącym `PickContextual`):

```csharp
    /// <summary>
    /// Margines na skali CROSS-ENCODERA (sigmoid ~0..1) — inna skala niż <see cref="DefaultSignalMargin"/>
    /// (cosine), więc osobna stała, nie współdzielona liczba. Wartość startowa, NIE skalibrowana:
    /// jedyny pomiar (2026-08-11) to 0.8842 vs 0.0503 — rozdział o trzy rzędy wielkości, przy którym
    /// każdy sensowny margines daje ten sam wynik. Kalibracja: `--refusals` na zamrożonym zestawie
    /// (Retrieval:RerankSignalMargin, bez redeployu).
    /// </summary>
    public const double DefaultRerankSignalMargin = 0.05;

    /// <summary>
    /// Wybór wariantu follow-upu na DWÓCH rozdzielonych sygnałach. Gdy cross-encoder ocenił OBA
    /// warianty (Reranker:Enabled=true), decyduje ON — bo cosine przy foldzie kłamie: sklejka zawiera
    /// fragment poprzedniej odpowiedzi, więc mierzy podobieństwo do samej siebie i rośnie razem z
    /// długością foldu, nie z trafnością (zmierzone: fold 0.8576/0 trafnych vs surowe 0.8431/5 trafnych).
    /// Warunek „OBA": jednostronny sygnał jest nieporównywalny, więc wtedy zostaje cosine —
    /// nie zgadujemy. Reranker wyłączony → zachowanie dokładnie jak dotąd (razem z jego wadami).
    /// Asymetria (surowy musi POBIĆ kontekstowy o margines) zostaje na obu skalach: uzasadnienie
    /// kosztowe z <see cref="PickContextual(double, double, double)"/> nie zależy od tego, kto sądzi.
    /// </summary>
    public static bool PickContextual(
        RetrievalResult raw, RetrievalResult contextual, double cosineMargin, double rerankMargin)
        => raw.RerankTopScore is { } rawRerank && contextual.RerankTopScore is { } ctxRerank
            ? rawRerank <= ctxRerank + rerankMargin
            : PickContextual(raw.MaxSimilarity, contextual.MaxSimilarity, cosineMargin);
```

- [ ] **Krok 4: Uruchom testy i potwierdź, że przechodzą**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~FollowUpQueryTests"
```
Oczekiwane: PASS, wszystkie testy klasy (stare + 5 nowych).

- [ ] **Krok 5: Commit**

```bash
git add src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs tests/PrawoRAG.Tests/Retrieval/FollowUpQueryTests.cs
git commit -m "feat(retrieval): wybor wariantu follow-upu na sygnale cross-encodera"
```

---

### Zadanie 3: `FollowUpSelector` — jedna orkiestracja zamiast trzech rozjechanych kopii

**Pliki:**
- Create: `src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs`
- Create: `tests/PrawoRAG.Tests/Fakes/FakeRetriever.cs`
- Create: `tests/PrawoRAG.Tests/Retrieval/FollowUpSelectorTests.cs`
- Modify: `src/PrawoRAG.Api/Services/ChatService.cs:35-52`
- Modify: `src/PrawoRAG.Api/Program.cs:255-271` oraz `RetrievalOptions` (linie ~348-366)
- Modify: `src/PrawoRAG.Eval/RefusalEvalRunner.cs:95` i `:147-166`

**Interfejsy:**
- Consumes: `FollowUpQuery.PickContextual(RetrievalResult, RetrievalResult, double, double)` (Zadanie 2),
  `RetrievalQuery.RerankText` (Zadanie 1).
- Produces:
  - `FollowUpSelector.Selection` — `sealed record Selection(RetrievalQuery Query, RetrievalResult Result, bool UsedContextual)`
  - `FollowUpSelector.SelectAsync(IRetriever retriever, Func<string, RetrievalQuery> queryFactory, string question, IReadOnlyList<ChatTurn> history, double cosineMargin, double rerankMargin, CancellationToken ct)` → `Task<Selection>`
  - `RetrievalOptions.RerankSignalMargin` (`double`, domyślnie `FollowUpQuery.DefaultRerankSignalMargin`)

- [ ] **Krok 1: Napisz `FakeRetriever`**

`tests/PrawoRAG.Tests/Fakes/FakeRetriever.cs`:

```csharp
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Deterministyczny <see cref="IRetriever"/>: zwraca wynik wybrany funkcją po tekście zapytania i
/// zapamiętuje WSZYSTKIE otrzymane zapytania. Pozwala testować orkiestrację follow-upu (który tekst
/// trafia do którego toru) bez Postgresa i bez TEI.
/// </summary>
public sealed class FakeRetriever(Func<RetrievalQuery, RetrievalResult> respond) : IRetriever
{
    public List<RetrievalQuery> Queries { get; } = [];

    public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        return Task.FromResult(respond(query));
    }
}
```

- [ ] **Krok 2: Napisz testy selektora, które mają paść**

`tests/PrawoRAG.Tests/Retrieval/FollowUpSelectorTests.cs`:

```csharp
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Tests.Fakes;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-FUS — jedyna orkiestracja follow-upu (dawniej trzy kopie: ChatService, /api/chat, RefusalEvalRunner,
/// z których ta trzecia zdążyła się rozjechać). Bez DB/TEI: <see cref="FakeRetriever"/> odpowiada po
/// tekście zapytania, więc asercje dotyczą TEGO, co jest tu naprawdę logiką — jakie teksty trafiają do
/// jakich torów i który wariant wygrywa.
/// </summary>
public class FollowUpSelectorTests
{
    private const string Q1 = "Co grozi za wyciek danych osobowych z systemów medycznych?";
    private const string Q2 = "A co powinienem zrobić, jeżeli do wycieku doszło?";

    private static readonly IReadOnlyList<ChatTurn> History =
        [new(Q1, "Źródła nie określają sankcji. Podmioty prowadzące bazy danych w ochronie zdrowia…",
            ["Ustawa o systemie informacji w ochronie zdrowia, art. 37"])];

    private static RetrievalQuery Factory(string text) => new() { Text = text, TopK = 8 };

    [Fact] // Pusta historia = jeden retrieval, bez wariantu kontekstowego (zero kosztu dla zwykłych pytań).
    public async Task No_history_retrieves_once_and_uses_raw()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.80, 0.90));
        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q1, [],
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.Single(retriever.Queries);
        Assert.False(sel.UsedContextual);
        Assert.Equal(Q1, sel.Query.Text);
    }

    [Fact] // Wariant kontekstowy dostaje fold w Text, ale surowe pytanie w RerankText i ExactMatchText.
    public async Task Contextual_query_keeps_raw_question_for_judge_and_exact_lanes()
    {
        var retriever = new FakeRetriever(_ => new RetrievalResult([], 0.80, 0.90));
        await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        var ctx = retriever.Queries[1];
        Assert.Contains("art. 37", ctx.Text);                 // fold zostaje w torze gęstym/BM25
        Assert.Equal(Q2, ctx.RerankText);                     // sędzia sądzi po pytaniu użytkownika
        Assert.DoesNotContain("art. 37", ctx.EffectiveExactMatchText); // tory dokładne bez foldu
    }

    [Fact] // Zmierzony przypadek: fold ma wyższy cosine, ale reranker go demaskuje → wygrywa surowy.
    public async Task Misleading_fold_loses_on_rerank_signal()
    {
        var retriever = new FakeRetriever(q => q.RerankText is null
            ? new RetrievalResult([], 0.8431, 0.8842)   // surowy
            : new RetrievalResult([], 0.8576, 0.0503)); // kontekstowy

        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.False(sel.UsedContextual);
        Assert.Equal(Q2, sel.Query.Text);
    }

    [Fact] // Bez rerankera decyduje cosine — dokładnie dzisiejsze zachowanie (i dzisiejszy bug).
    public async Task Without_reranker_keeps_cosine_behaviour()
    {
        var retriever = new FakeRetriever(q => q.RerankText is null
            ? new RetrievalResult([], 0.8431)
            : new RetrievalResult([], 0.8576));

        var sel = await FollowUpSelector.SelectAsync(retriever, Factory, Q2, History,
            FollowUpQuery.DefaultSignalMargin, FollowUpQuery.DefaultRerankSignalMargin, default);

        Assert.True(sel.UsedContextual);
    }
}
```

- [ ] **Krok 3: Uruchom testy i potwierdź, że nie kompilują**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~FollowUpSelectorTests"
```
Oczekiwane: błąd kompilacji — brak typu `FollowUpSelector`.

- [ ] **Krok 4: Napisz `FollowUpSelector`**

`src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs`:

```csharp
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// JEDYNA orkiestracja follow-upu: podwójny retrieval (surowy vs kontekstowy) + wybór wariantu.
/// Istnieje, bo ta sama logika żyła w trzech kopiach (ChatService, endpoint /api/chat,
/// RefusalEvalRunner) i zdążyła się rozjechać — runner evalowy wołał starą przeciążkę bez foldu,
/// więc metryka mierzyła INNY pipeline niż produkcja, wbrew własnemu komentarzowi „rozjazd
/// z ChatService = rozjazd metryki". Kształt zapytań (TopK, filtry, MinChunkTokens) wstrzykuje
/// caller przez <paramref name="queryFactory"/> — każda ścieżka buduje je inaczej i to zostaje jej.
/// </summary>
public static class FollowUpSelector
{
    /// <summary>Wybrany wariant: zapytanie (do augmentera — niesie cytaty z historii), jego wynik
    /// i informacja, czy wygrał wariant kontekstowy (diagnostyka/eval).</summary>
    public sealed record Selection(RetrievalQuery Query, RetrievalResult Result, bool UsedContextual);

    public static async Task<Selection> SelectAsync(
        IRetriever retriever,
        Func<string, RetrievalQuery> queryFactory,
        string question,
        IReadOnlyList<ChatTurn> history,
        double cosineMargin,
        double rerankMargin,
        CancellationToken ct)
    {
        var rawQuery = queryFactory(question);
        var rawResult = await retriever.RetrieveAsync(rawQuery, ct);
        if (history.Count == 0) return new Selection(rawQuery, rawResult, UsedContextual: false);

        // SEKWENCYJNIE — wspólny scoped DbContext nie jest thread-safe (nie zrównoleglać).
        var ctxQuery = queryFactory(FollowUpQuery.Contextualize(history, question)) with
        {
            // Tory DOKŁADNE: tylko pytania użytkownika — sygnatura/artykuł z ODPOWIEDZI systemu nie
            // może udawać jawnego asku (bug: kotwice wyroków zalewały TopK).
            ExactMatchText = FollowUpQuery.ContextualizeForExactMatch(history, question),
            // SĘDZIA: surowe pytanie — inaczej sklejka ocenia samą siebie (patrz RetrievalQuery.RerankText).
            RerankText = question,
        };
        var ctxResult = await retriever.RetrieveAsync(ctxQuery, ct);

        return FollowUpQuery.PickContextual(rawResult, ctxResult, cosineMargin, rerankMargin)
            ? new Selection(ctxQuery, ctxResult, UsedContextual: true)
            : new Selection(rawQuery, rawResult, UsedContextual: false);
    }
}
```

- [ ] **Krok 5: Uruchom testy i potwierdź, że przechodzą**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~FollowUpSelectorTests"
```
Oczekiwane: PASS, 4 testy.

- [ ] **Krok 6: Dodaj `RerankSignalMargin` do `RetrievalOptions`**

W `src/PrawoRAG.Api/Program.cs`, w klasie `RetrievalOptions`, pod `FollowUpSignalMargin`:

```csharp
    /// <summary>Margines sygnału przy follow-upach na skali cross-encodera (używany, gdy
    /// Reranker:Enabled=true i OBA warianty mają score). Inna skala niż
    /// <see cref="FollowUpSignalMargin"/> — patrz <see cref="FollowUpQuery.DefaultRerankSignalMargin"/>.</summary>
    public double RerankSignalMargin { get; set; } = FollowUpQuery.DefaultRerankSignalMargin;
```

- [ ] **Krok 7: Podepnij `ChatService`**

W `src/PrawoRAG.Api/Services/ChatService.cs` zamień cały blok linii 41–52 (od `var query = Query(question);`
do zamykającej klamry `if (history.Count > 0) { … }`) na:

```csharp
        var selection = await FollowUpSelector.SelectAsync(
            retriever, Query, question, history, o.FollowUpSignalMargin, o.RerankSignalMargin, ct);
        var (query, result) = (selection.Query, selection.Result);
```

Komentarz nad tym blokiem (linie 35–40) zastąp:

```csharp
        // Follow-upy: dopytanie („a co z § 2?") samo embeduje się bezwartościowo, więc retrieval liczony
        // 2x (surowy vs kontekstowy) i wybór wariantu — CAŁOŚĆ w FollowUpSelector, wspólnym dla /api/chat,
        // tego serwisu i evalu. Nie kopiować tej logiki z powrotem: rozjazd kopii = rozjazd metryki.
```

- [ ] **Krok 8: Podepnij endpoint `/api/chat`**

W `src/PrawoRAG.Api/Program.cs` zamień CAŁY blok linii 255–271 — komentarz „Follow-upy (parytet
z UI/ChatService)…" wraz z kodem od `var q = ToQuery(req.Question, …)` do zamykającej klamry
`if (history.Count > 0) { … }` — na:

```csharp
        // Follow-upy: podwójny retrieval + wybór wariantu — wspólny FollowUpSelector (parytet z UI/evalem).
        var selection = await FollowUpSelector.SelectAsync(
            retriever, text => ToQuery(text, req.Filters, o.TopK, o), req.Question, history,
            o.FollowUpSignalMargin, o.RerankSignalMargin, ct);
        var (q, result) = (selection.Query, selection.Result);
```

Blok `var history = …` (linie 250–253) i wszystko od `// BRAMKA ABSTYNENCJI` (linia 273) w dół
zostają nietknięte — dalszy kod używa `q` i `result` pod tymi samymi nazwami.

- [ ] **Krok 9: Podepnij `RefusalEvalRunner` (koniec rozjazdu metryki)**

W `src/PrawoRAG.Eval/RefusalEvalRunner.cs` zamień blok linii 157–166 na:

```csharp
        var selection = await FollowUpSelector.SelectAsync(
            retriever, Query, item.Question, item.History, margin, rerankMargin, ct);
        var (query, result) = (selection.Query, selection.Result);
```

Do sygnatury `ReplayAsync` (linia 150–152) dopisz parametr `double rerankMargin` bezpośrednio po
`double margin`, a w `RunAsync` (obok istniejącego odczytu `margin`, linia ~48) dodaj:

```csharp
        var rerankMargin = cfg.GetValue<double?>("Retrieval:RerankSignalMargin")
                           ?? FollowUpQuery.DefaultRerankSignalMargin;
```

`ReplayAsync` ma DOKŁADNIE jedno wywołanie — linia 95; dopisz tam `rerankMargin` po `margin`:

```csharp
            var r = await ReplayAsync(scope.ServiceProvider, item, generate, topK, threshold, minChunkTokens, margin, rerankMargin, ct);
```

Komentarz nad `ReplayAsync` (linie 147–149) zastąp:

```csharp
    /// <summary>Odtworzenie logiki ChatService (FollowUpSelector → bramka → augmenter → OrderForGrounding
    /// → prompt → LLM). Wybór wariantu follow-upu jest WSPÓŁDZIELONY z produkcją (FollowUpSelector) —
    /// wcześniej ten runner miał własną, uboższą kopię (bez foldu i bez ExactMatchText), więc metryka
    /// mierzyła inny pipeline niż czat.</summary>
```

- [ ] **Krok 10: Zbuduj całość i uruchom pełne testy**

```bash
dotnet build PrawoRAG.slnx
dotnet test tests/PrawoRAG.Tests
```
Oczekiwane: build bez błędów (ostrzeżenie NU1903 o `Microsoft.OpenApi` jest znane i niezwiązane),
wszystkie testy PASS.

- [ ] **Krok 11: Sprawdź, że nie została żadna kopia**

```bash
grep -rn "PickContextual" src/ --include="*.cs" | grep -v obj/
```
Oczekiwane: TYLKO definicje w `FollowUpQuery.cs` i jedno wywołanie w `FollowUpSelector.cs`.
Jeśli widać `PickContextual` w `ChatService.cs`, `Program.cs` albo `RefusalEvalRunner.cs` — kopia
została, wróć do kroków 7–9.

- [ ] **Krok 12: Commit**

```bash
git add src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs \
        src/PrawoRAG.Api/Services/ChatService.cs src/PrawoRAG.Api/Program.cs \
        src/PrawoRAG.Eval/RefusalEvalRunner.cs \
        tests/PrawoRAG.Tests/Fakes/FakeRetriever.cs \
        tests/PrawoRAG.Tests/Retrieval/FollowUpSelectorTests.cs
git commit -m "refactor(retrieval): jeden FollowUpSelector zamiast trzech rozjechanych kopii"
```

---

### Zadanie 4: most cytowań za bramką cross-encodera

**Pliki:**
- Modify: `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs:155-218` (kolejność) oraz `:227` (stałe)
- Test: `tests/PrawoRAG.Tests/Retrieval/CitationBridgeTests.cs` (dopisz na końcu klasy)

**Interfejsy:**
- Consumes: `RetrievalQuery.EffectiveRerankText` (Zadanie 1).
- Produces: zmienione zachowanie `HybridRetriever` — brak nowych typów publicznych.

**Dlaczego tak:** most istnieje, bo przepis rządzący bywa nieretrievalny semantycznie, a orzeczenia
same go cytują. Ale dziś (a) głosować może KAŻDY kandydat po fuzji RRF, także oceniony przez
cross-encoder na 0,17, i (b) zwycięzca dostaje slot z pominięciem rerankera
(`exact.Concat(bridge).Concat(ranked)`). To zadanie jest PREWENCYJNE — zamyka dziurę widoczną w
kodzie i w zmierzonych score'ach puli z pytania 1. **Nie jest fixem KK art. 64 § 1-3**: sonda
wykluczyła most jako źródło tamtego objawu (patrz korekta #2 w diagnozie wyżej), a przyczyna
pozostaje niezdiagnozowana. Jeśli po wdrożeniu tego zadania KK art. 64 nadal się pojawia — to
oczekiwane, nie regresja.

- [ ] **Krok 1: Napisz testy, które mają paść**

Wzorzec jak w istniejących testach klasy: FIKCYJNY numer artykułu (`9997`) i tytuł aktu z dopiskiem
testowym — inaczej scenariusz zderzy się z prawdziwym korpusem na współdzielonej bazie. Sygnałem, że
zadziałał MOST (a nie tor gęsty, który przy `FakeEmbeddingProvider` i tak wciąga seedowany akt), jest
`Score == double.MaxValue`. W `tests/PrawoRAG.Tests/Retrieval/CitationBridgeTests.cs`, przed zamykającą
klamrą klasy:

```csharp
    [Fact] // M6: kandydat oceniony nisko przez cross-encoder NIE GŁOSUJE → 1 realny głos < próg 2
    public async Task Irrelevant_candidates_do_not_vote_in_bridge()
    {
        const string src = "TEST-MOST-6";
        await CleanAllAsync();
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako6)",
            "Treść przepisu testowego mostu. Mostakoszescprzepis.", articleNo: "9997");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 11/24",
            "Mostako6 Mostakoistotny wywód na temat pytania; podstawą jest art. 9997 k.c. i wina sprawcy.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 12/24",
            "Mostako6 zupełnie inny wątek o wiarygodności świadka; sąd wspomniał art. 9997 k.c. na marginesie.");

        await using var db = NewDb();
        // Tylko j1 jest istotny dla pytania — j2 dostaje niski score i traci prawo głosu.
        var reranker = new FakeReranker("Mostakoistotny");
        var res = await new HybridRetriever(db, Emb, reranker).RetrieveAsync(
            new RetrievalQuery { Text = "Mostako6", MinChunkTokens = 0 }, default);

        Assert.DoesNotContain(res.Chunks, c => c.Text.Contains("Mostakoszescprzepis") && c.Score == double.MaxValue);
        await CleanAsync(src);
    }

    [Fact] // M7: gdy głosują kandydaci ISTOTNI, most dalej dociąga przepis — feature nie gaśnie po cichu
    public async Task Bridge_still_fires_when_relevant_candidates_vote()
    {
        const string src = "TEST-MOST-7";
        await CleanAllAsync();
        await SeedAsync(src, "kc", DocTypes.Act, "Kodeks cywilny (testowy Mostako7)",
            "Mostakosiodmy kto z winy swej wyrządził drugiemu szkodę. Mostakosiodmyprzepis.", articleNo: "9997");
        await SeedAsync(src, "j1", DocTypes.Judgment, "SO w Testowie I C 13/24",
            "Mostako7 Mostakosiodmy wichura przewróciła drzewo; podstawą jest art. 9997 k.c. i wina właściciela.");
        await SeedAsync(src, "j2", DocTypes.Judgment, "SR w Testowie I C 14/24",
            "Mostako7 Mostakosiodmy topola runęła na altankę; sąd rozważał przesłanki z art. 9997 k.c.");

        await using var db = NewDb();
        var reranker = new FakeReranker("Mostakosiodmy");   // OBA orzeczenia istotne → 2 głosy
        var res = await new HybridRetriever(db, Emb, reranker).RetrieveAsync(
            new RetrievalQuery { Text = "Mostako7", MinChunkTokens = 0 }, default);

        Assert.Contains(res.Chunks, c => c.Text.Contains("Mostakosiodmyprzepis") && c.Score == double.MaxValue);
        await CleanAsync(src);
    }
```

- [ ] **Krok 2: Uruchom testy i potwierdź, że pierwszy pada**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~CitationBridgeTests"
```
Oczekiwane: `Irrelevant_candidates_do_not_vote_in_bridge` FAIL (dziś głosują wszyscy, więc most
promuje przepis mimo jednego istotnego głosu), `Bridge_still_fires_when_relevant_candidates_vote` PASS.

- [ ] **Krok 3: Dodaj próg istotności wyborcy**

W `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs`, obok `BridgeMinDocVotes` (linia ~227):

```csharp
    /// <summary>
    /// Jaką część score'u NAJLEPSZEGO kandydata musi mieć pasaż, żeby głosować w moście cytowań.
    /// Dziś głosowali wszyscy po fuzji RRF — także pasaże ocenione na 0,17, które przegłosowały
    /// KK art. 64 (recydywa) w pytaniu o zgłoszenie wycieku danych (pomiar 2026-08-11). Próg jest
    /// WZGLĘDNY (ułamek topu), nie absolutny, z tego samego powodu, dla którego bramka abstynencji
    /// nie stoi na score rerankera: przy śmieciowej puli cross-encoder klastruje ~0,99 i absolutna
    /// liczba nic nie znaczy. Przy takiej puli próg względny nikogo nie odcina — degradacja do
    /// dzisiejszego zachowania, zamiast losowego cięcia. Bez rerankera głosują wszyscy, jak dotąd.
    /// </summary>
    private const double BridgeVoterScoreFraction = 0.5;
```

- [ ] **Krok 4: Przestaw kolejność — most po rerankingu, bez gwarantowanego slotu**

W `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs`:
1. USUŃ dotychczasowe wyliczenie mostu wraz z jego komentarzem (linie ~198–202, blok kończący się
   `var bridge = await CitationBridgeAsync(query, deduped, ct);`) — most przenosi się ZA `exact`,
   bo potrzebuje wyniku rerankingu, którego wcześniej nie miał.
2. USUŃ dotychczasowy blok `var final = exact.Concat(bridge).Concat(ranked)…` (linie ~213–217).
3. W miejsce po wyliczeniu `exact` (czyli po `var exact = ExactMatchCap.LimitPerDocument(…);`,
   linia ~211) wstaw:

```csharp
        // Most cytowań: przepis rządzący dociągnięty z cytowań w trafionych orzeczeniach. Głosują TYLKO
        // kandydaci istotni wg cross-encodera (BridgeVoterScoreFraction topu). Bez rerankera — wszyscy.
        var voterFloor = rerankTop * BridgeVoterScoreFraction;
        var voters = voterFloor is { } floor
            ? ranked.Where(c => c.RerankScore >= floor).ToList()
            : ranked;
        var bridge = await CitationBridgeAsync(query, voters, ct);

        // Most NIE dostaje już gwarantowanego slotu przed semantyką: dociągnięty przepis przechodzi przez
        // tego samego sędziego co reszta i wchodzi tam, gdzie zasłużył. Wcześniej wstrzykiwał się na
        // wierzch z pominięciem rerankera, więc jeden fałszywy zwycięzca głosowania zajmował sloty [1..3].
        // Drugie wywołanie cross-encodera dotyczy garstki pasaży (≤ CitationBridgeArticles ×
        // BridgeChunksPerArticle) i tylko gdy most cokolwiek zwrócił.
        if (reranker is not null && bridge.Count > 0)
        {
            var bridgeScores = await reranker.RerankAsync(
                query.EffectiveRerankText, bridge.Select(c => c.Text).ToList(), ct);
            var byIndex = bridgeScores.ToDictionary(x => x.Index, x => x.Score);
            // Most PRZED `ranked` w konkatenacji: gdy ten sam chunk przyszedł oboma torami, ma zostać
            // wersja mostu (Score=double.MaxValue — po tym markerze testy i diagnostyka poznają, że to
            // most dociągnął przepis, a nie tor gęsty). Dopiero po dedupie sortujemy po sędzim.
            ranked = bridge
                .Select((c, i) => c with { RerankScore = byIndex.GetValueOrDefault(i) })
                .Concat(ranked)
                .GroupBy(c => c.ChunkId).Select(g => g.First())
                .OrderByDescending(c => c.RerankScore ?? double.MinValue)
                .ToList();
            bridge = [];   // most jest już wtopiony w `ranked` — nie dokładać go drugi raz
        }

        // Kolejność slotów: SYGNATURA/AKT (najbardziej konkretny ask) → cytat strukturalny → most → semantyka.
        var final = exact.Concat(bridge).Concat(ranked)
            .GroupBy(c => c.ChunkId).Select(g => g.First()) // dedup; wcześniejsze tory wygrywają slot
            .Take(query.TopK)
            .ToList();
```

Uwaga 1: `ranked` jest dziś deklarowane jako `List<RetrievedChunk> ranked;` (linia ~166) — zostaje,
zmienna musi być przypisywalna. `bridge` zadeklaruj jako `var bridge = …` (już tak jest).

Uwaga 2 (ŚWIADOMA, nie przeoczenie): `rerankTop` zwracany w `RetrievalResult` pozostaje wartością
sprzed wtopienia mostu, więc chunk mostu o wyższym score go nie podniesie. Tak ma być z dwóch
powodów: (a) `voterFloor` musi być liczony z puli, która GŁOSOWAŁA, inaczej próg zależałby od tego,
kogo most dociągnął; (b) sygnał wraca do `FollowUpQuery.PickContextual`, gdzie oba warianty liczą go
identycznie — porównanie zostaje uczciwe. Nie „naprawiaj" tego bez pomiaru: przesunie decyzję
follow-upu.

- [ ] **Krok 5: Uruchom testy i potwierdź, że przechodzą**

```bash
dotnet test tests/PrawoRAG.Tests --filter "FullyQualifiedName~CitationBridgeTests"
```
Oczekiwane: oba nowe testy PASS oraz WSZYSTKIE dotychczasowe testy klasy PASS (most nie może
przestać działać w scenariuszach bez rerankera).

- [ ] **Krok 6: Regresja mostu na żywym korpusie (art. 415 KC)**

Most powstał dla przypadku „art. 415 KC nieretrievalny dla pytań opisowych" (sonda 2026-07-18: trzy
niezależne orzeczenia w top-30). `BridgeVoterScoreFraction = 0.5` mógł odciąć część jego wyborców,
więc sprawdź to POMIAREM, nie założeniem — z żywym TEI i rerankerem:

```bash
Reranker__Enabled=true Reranker__BaseUrl=http://localhost:8081 \
  dotnet run --project src/PrawoRAG.Eval -- --probe-akty \
  "czy odpowiadam za szkodę wyrządzoną przez drzewo, które spadło na samochód sąsiada"
```
Oczekiwane: `DU/1964/93` art. `415` nadal pojawia się w źródłach. Jeśli ZNIKNĄŁ — obniż
`BridgeVoterScoreFraction` do `0.3`, powtórz sondę i zapisz w komentarzu stałej ORAZ w opisie commita
zmierzoną wartość graniczną. Jeśli nawet `0.3` nie przywraca art. 415 — zatrzymaj się i zgłoś to
właścicielowi: znaczyłoby, że wyborcy mostu są dla cross-encodera nieistotni, co podważa cały most,
a to decyzja produktowa, nie implementacyjna.

- [ ] **Krok 7: Commit**

```bash
git add src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs tests/PrawoRAG.Tests/Retrieval/CitationBridgeTests.cs
git commit -m "fix(retrieval): most cytowan za bramka cross-encodera - koniec slotow dla smieciowych glosow"
```

---

### Zadanie 5: pozycje pomiarowe w golden secie + sprzątnięcie sondy tymczasowej

**Pliki:**
- Modify: `src/PrawoRAG.Eval/golden-set.json`
- Delete: `src/PrawoRAG.Eval/_FollowUpProbe.cs` (nieśledzony przez gita)
- Modify: `src/PrawoRAG.Eval/Program.cs` (usuń dyspozytor `--probe-followup`, linie ~79-80)

- [ ] **Krok 1: Dopisz dwie pozycje do golden setu**

W `src/PrawoRAG.Eval/golden-set.json`, przed zamykającym `]`, po ostatniej pozycji (pamiętaj o przecinku
kończącym poprzednią pozycję):

```json
  { "id": "uodo-107", "question": "Co grozi za wyciek danych osobowych z systemów informatycznych odpowiedzialnych za dane medyczne ?", "category": "InCorpus", "shouldAbstain": false, "expectedEli": "DU/2018/1000", "expectedArticle": "107", "note": "Pomiar 2026-08-11: art. 107 ust. 2 (dane o zdrowiu, do 3 lat) jest w korpusie ze 138 chunkami aktu, ale NIE wchodzi do top-50 (cosine 0.7620 przy progu top-400 ~0.778). Pozycja jest ŚWIADOMIE czerwona - to miernik luki recall dla pytań potocznych, nie test do zzielenienia. NIE usuwać jej, gdy Recall@K spadnie; recall to metryka, nie bramka pass/fail." },
  { "id": "uodo-60", "question": "A co powinienem zrobić według głównego inspektora danych osobowych jeżeli do takiego wycieku doszło ?", "category": "InCorpus", "shouldAbstain": false, "expectedEli": "DU/2018/1000", "expectedArticle": "60", "note": "Pomiar 2026-08-11: to pytanie SAMODZIELNIE trafia (5/8 slotów w uodo, art. 58/60/64/91/99). Strażnik regresji dla R1-R2: gdyby spadło, znaczy że zmiana w wyborze wariantu albo w moście popsuła działający przypadek. Brzmienie zachowane DOSŁOWNIE (z przestarzałym 'głównym inspektorem'), bo na nim zmierzono liczbę odniesienia." }
```

- [ ] **Krok 2: Uruchom eval retrieval-only i zapisz liczby odniesienia**

```bash
Reranker__Enabled=true Reranker__BaseUrl=http://localhost:8081 \
  dotnet run --project src/PrawoRAG.Eval 2>&1 | tee /tmp/eval-po-r1r2.txt
```
Oczekiwane: przebieg kończy się raportem z `Recall@K`. Sprawdź w wyjściu obie nowe pozycje:
`uodo-60` MA trafiać (`DU/2018/1000`), `uodo-107` NIE (znana luka). Zapisz obie obserwacje —
wchodzą do opisu commita.

- [ ] **Krok 3: Usuń tymczasową sondę follow-upu**

Sonda była oznaczona „TYMCZASOWE" i zakodowana na sztywno pod zamknięty przypadek art. 1a u.p.o.l.
(commity `aa48637`, `169fc02`). Jej rolę przejmują: deterministyczne `FollowUpSelectorTests`
(Zadanie 3) i zamrożony zestaw `--refusals`, który niesie realną historię rozmów.

```bash
rm src/PrawoRAG.Eval/_FollowUpProbe.cs
```

W `src/PrawoRAG.Eval/Program.cs` usuń komentarz i blok dyspozytora `--probe-followup` (linie ~79-83,
od `// TYMCZASOWE — diagnoza follow-up` do zamykającej klamry tego `if`).

- [ ] **Krok 4: Zbuduj i uruchom testy**

```bash
dotnet build PrawoRAG.slnx
dotnet test tests/PrawoRAG.Tests
```
Oczekiwane: build bez błędów (brak odwołań do `FollowUpProbe`), testy PASS.

- [ ] **Krok 5: Commit**

```bash
git add src/PrawoRAG.Eval/golden-set.json src/PrawoRAG.Eval/Program.cs
git add -A src/PrawoRAG.Eval/
git commit -m "test(eval): pozycje uodo-107/uodo-60 w golden secie; usuniecie tymczasowej sondy follow-up"
```

---

### Zadanie 6: kalibracja marginesu + usunięcie kotwic z foldu (R3)

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs` (margines + usunięcie kotwic ze sklejki)
- Modify: `src/PrawoRAG.Domain/Llm/ChatTurn.cs` (usunięcie `SourceAnchors`)
- Modify: `src/PrawoRAG.Api/Components/Pages/Chat.razor:337-343` i `:434`
- Modify: `src/PrawoRAG.Api/Program.cs:252` i `:347` (`HistoryTurnDto` bez `SourceAnchors`)
- Modify: `src/PrawoRAG.Domain/Retrieval/FollowUpSelector.cs` (tymczasowe logowanie, zdjęte w Kroku 6)
- Modify: `tests/PrawoRAG.Tests/Retrieval/FollowUpSelectorTests.cs`, `.../FollowUpQueryTests.cs`
- Create: `docs/POMIARY-ODSIEW-SZUMU-2026-08-11.md`

**Warunek wstępny:** Zadania 1–5 zmergowane. Bez nich `--refusals` mierzy inny pipeline niż produkcja.

- [ ] **Krok 1: Zamroź zestaw pomiarowy (jeśli jeszcze nie zamrożony)**

```bash
ls src/PrawoRAG.Eval/refusal-set.json || \
  dotnet run --project src/PrawoRAG.Eval -- --refusals --freeze
```
Oczekiwane: plik istnieje; runner wypisuje `Zestaw ZAMROŻONY: … (N pytań, odcisk …)`. Odcisk MUSI być
identyczny we wszystkich biegach niżej — inaczej porównujesz różne zestawy.

- [ ] **Krok 2: Zmierz trzy warianty marginesu rerankera**

```bash
for m in 0.02 0.05 0.15; do
  echo "=== RerankSignalMargin=$m ==="
  Reranker__Enabled=true Reranker__BaseUrl=http://localhost:8081 Retrieval__RerankSignalMargin=$m \
    dotnet run --project src/PrawoRAG.Eval -- --refusals 2>&1 | tail -30
done | tee /tmp/kalibracja-rerank-margin.txt
```
Oczekiwane: dla każdego marginesu raport z odsetkiem poprawnych odmów i rozkładem źródeł
(`Acts`/`Judgments`). Wybierz wartość, przy której nie rośnie liczba fałszywych odmów, a rośnie
udział aktów. Jeśli wszystkie trzy dają ten sam wynik — zapisz to i ZOSTAW `0.05`; brak różnicy to
też wynik, a nie powód do majsterkowania.

- [ ] **Krok 3: Wstaw skalibrowaną wartość**

Jeśli wygrała wartość inna niż `0.05`, zmień `FollowUpQuery.DefaultRerankSignalMargin` na wybraną
i dopisz do jej komentarza XML jedno zdanie: datę kalibracji, odcisk zestawu i zmierzoną różnicę.
Jeśli wygrało `0.05` — zamień w komentarzu „Wartość startowa, NIE skalibrowana" na zdanie o
przeprowadzonej kalibracji z tymi samymi danymi.

- [ ] **Krok 4: Usuń KOTWICE z foldu (R3) — to jest ta redukcja**

Nie jest to decyzja warunkowa: pomiar z diagnozy wyżej jest jednoznaczny. Kotwica źródeł niesie numer
Dziennika Ustaw i numer artykułu poprzedniej tury, więc wyzwala tory dokładne wszędzie, gdzie nie ma
`ExactMatchText` (zmierzone: 8/8 slotów `/api/search` ze `Score = double.MaxValue`), a tam gdzie tory
dokładne są odcięte — dominuje BM25 tytułem aktu. Fragment odpowiedzi ZOSTAJE: to on wyciągnął
uodo art. 107 na pozycję #2, przepis nieobecny w top-50 dla samego pytania.

Usuń w tej kolejności, uruchamiając `dotnet build PrawoRAG.slnx` po każdym kroku:

1. W `FollowUpQuery.Contextualize(IReadOnlyList<ChatTurn>, string)` usuń zmienną `anchors`
   i jej człon ze sklejki — zostaje `baseCtx`, `cites`, `snippet`:

```csharp
        return string.Join(" ", new[] { baseCtx, cites, snippet }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
```

   i dopisz do komentarza XML metody:

```csharp
    /// Kotwice źródeł USUNIĘTE 2026-08-11: niosły numer Dz.U. i numer artykułu poprzedniej tury, więc
    /// w każdej ścieżce bez ExactMatchText wyzwalały tory DOKŁADNE i zjadały cały budżet slotów aktem
    /// z poprzedniej tury (zmierzone: 8/8 slotów ze Score=MaxValue), a tam gdzie tory dokładne są
    /// odcięte — dominowały BM25 tytułem aktu. Sam fragment odpowiedzi (bez kotwic) w tym samym
    /// pomiarze wyciągnął uodo art. 107 na #2 — dlatego zostaje.
```

2. Usuń `ChatTurn.SourceAnchors` (`src/PrawoRAG.Domain/Llm/ChatTurn.cs`) — po kroku 1 nie ma już
   czytelnika tego pola.
3. Usuń `SourceAnchors` i `FollowUpSourceAnchorsTaken` w `src/PrawoRAG.Api/Components/Pages/Chat.razor:337-343`
   oraz jego użycie w `:434` (zostaje `new ChatTurn(e.Question, e.Answer)`).
4. Usuń parametr `SourceAnchors` z `HistoryTurnDto` (`src/PrawoRAG.Api/Program.cs:347`) i z budowy
   `ChatTurn` w endpointcie (`:252` — zostaje `new ChatTurn(t.Question, t.Answer)`).
5. W `tests/PrawoRAG.Tests/Retrieval/FollowUpSelectorTests.cs` popraw dane i asercję: z `History` usuń
   trzeci argument (listę kotwic), a `Assert.Contains("art. 37", ctx.Text)` zamień na
   `Assert.Contains("Źródła nie określają sankcji", ctx.Text)` — fragment odpowiedzi ma zostać w sklejce.
   Popraw analogicznie testy foldu w `FollowUpQueryTests.cs`, jeśli asertują kotwice.

```bash
dotnet test tests/PrawoRAG.Tests
```
Oczekiwane: PASS.

- [ ] **Krok 5: Sprawdź, czy fold (już bez kotwic) w ogóle jeszcze wygrywa**

W `FollowUpSelector.SelectAsync`, tymczasowo, przed `return`:

```csharp
        if (Environment.GetEnvironmentVariable("PRAWORAG_LOG_FOLLOWUP") is { Length: > 0 })
            Console.WriteLine($"[followup] q=\"{question}\" " +
                              $"ctxWins={FollowUpQuery.PickContextual(rawResult, ctxResult, cosineMargin, rerankMargin)} " +
                              $"rawRerank={rawResult.RerankTopScore:F4} ctxRerank={ctxResult.RerankTopScore:F4}");
```

Pytanie w logu jest KONIECZNE — kryterium decyzji brzmi „czy wygrał przypadek, który realnie
potrzebuje kontekstu (anafora)", a tego nie da się odczytać z samego licznika.

```bash
PRAWORAG_LOG_FOLLOWUP=1 Reranker__Enabled=true Reranker__BaseUrl=http://localhost:8081 \
  dotnet run --project src/PrawoRAG.Eval -- --refusals 2>&1 | grep "\[followup\]" \
  | tee /tmp/fold-wins.txt
grep -c "ctxWins=True" /tmp/fold-wins.txt; grep -c "ctxWins=False" /tmp/fold-wins.txt
grep "ctxWins=True" /tmp/fold-wins.txt   # PRZECZYTAJ te pytania — czy to anafory?
```

Zapisz wynik w dokumencie pomiarów:
- **wygrywają anafory** („a co z § 2?", „a kim jest ta osoba?") → fold działa zgodnie z zamysłem, zostaje.
- **wygrywają pytania samodzielne** albo **zero zwycięstw** → zgłoś to właścicielowi jako kandydata do
  usunięcia całego foldu w kolejnej iteracji. NIE usuwaj go w ramach tego planu: to zdejmuje ścieżkę
  anafory z czterech plików i zasługuje na własną decyzję, nie na doklejkę do zadania kalibracyjnego.

- [ ] **Krok 6: Zapisz pomiary i zdejmij tymczasowe logowanie**

Usuń blok `PRAWORAG_LOG_FOLLOWUP` z `FollowUpSelector.cs`. Utwórz
`docs/POMIARY-ODSIEW-SZUMU-2026-08-11.md` zawierający: odcisk zamrożonego zestawu, tabelę trzech
marginesów z Kroku 2, liczby `ctxWins` z Kroku 4, podjętą decyzję o foldzie z uzasadnieniem oraz
wynik sondy art. 415 z Zadania 4 Krok 6. Bez tez — surowe liczby i decyzja.

- [ ] **Krok 7: Commit**

```bash
git add -A src/ docs/POMIARY-ODSIEW-SZUMU-2026-08-11.md tests/
git commit -m "feat(retrieval): kalibracja marginesu rerankera + decyzja o foldzie na pomiarach"
```

---

## Czego ten plan świadomie NIE robi

- **Nie pogłębia puli rerankera.** Art. 107 uodo leży w okolicach rangi ~1000 przy cosine 0.7620 wobec
  progu top-400 ~0.778 — pogłębienie do kilkuset nie sięgnie, a przy pytaniu 1 cross-encoder i tak nie
  rozdziela (art. 107 → 0.9947 vs parafraza tytułowa 0.9946).
- **Nie dodaje mapowań potoczne→ustawowe.** Wykluczone przez właściciela produktu.
- **Nie rusza reprezentacji chunków.** Prefiks tytułu aktu w każdym chunku prawdopodobnie ciągnie całą
  „ustawę o systemie informacji w ochronie zdrowia" w górę dla pytań o „systemy informatyczne … dane
  medyczne", ale zdjęcie go = re-embedding całego korpusu. Do podjęcia dopiero po pomiarze offline,
  że przesuwa art. 107 o wymagane ~0.09 cosine — osobny plan, nie ten.
- **Nie dokłada RODO.** Decyzja właściciela: prawo UE w późniejszym etapie. Do tego czasu pytanie 1
  pozostaje częściowo nieodpowiadalne i pozycja `uodo-107` w golden secie jest czerwona z tego powodu.
