using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Implementacja fasady czatu — ta sama logika co endpoint SSE /api/chat, ale in-process i jako strumień
/// <see cref="ChatEvent"/> dla Blazora. Rdzeń wartości (abstynencja + anty-fabrykacja) zostaje tu, nie w UI.
/// </summary>
public sealed class ChatService(
    IRetriever retriever, ITemporalAugmenter augmenter, ILlmProvider llm, IOptions<RetrievalOptions> options,
    IEmbeddingProvider embedder, IOptions<DocumentsOptions> documents,
    IIntentRouter? router = null, IOptions<GroundingOptions>? groundingOptions = null,
    IQueryReformulator? reformulator = null) : IChatService
{
    private readonly bool _documentsEnabled = documents.Value.Enabled;

    /// <summary>Brak wstrzykniętych opcji (starsze testy) = bramka WŁĄCZONA, jak w produkcji.</summary>
    private readonly GroundingOptions _grounding = groundingOptions?.Value ?? new GroundingOptions();

    /// <summary>Domyślna ścieżka (router decyduje) — jawnie na klasie, nie tylko jako domyślna metoda
    /// interfejsu, żeby istniejące wywołania po typie konkretnym (UI, testy) działały bez zmian.</summary>
    public IAsyncEnumerable<ChatEvent> AskAsync(
        string question, IReadOnlyList<ChatTurn> history, DocumentContext? document, CancellationToken ct)
        => AskAsync(question, history, document, forceRetrieval: false, ct);

    public async IAsyncEnumerable<ChatEvent> AskAsync(
        string question, IReadOnlyList<ChatTurn> history, DocumentContext? document,
        bool forceRetrieval, [EnumeratorCancellation] CancellationToken ct)
    {
        var chatSw = System.Diagnostics.Stopwatch.StartNew();
        var o = options.Value;

        // Kanał zdarzeń z CALLBACKÓW (Zadanie 3 planu ROU). AskAsync jest iteratorem asynchronicznym,
        // więc z callbacku (IProgress retrievalu, OnReasoningDelta providera) NIE DA SIĘ zrobić
        // `yield return` — a oba powstają wewnątrz `await`owanych wywołań. Callbacki piszą tutaj,
        // a główna pętla drenuje kanał w miejscach, gdzie może emitować.
        var side = Channel.CreateUnbounded<ChatEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        // SyncProgress, NIE Progress<T>: ten drugi dyspozycjonuje callback asynchronicznie, więc etapy
        // docierały po pracy, którą opisują, i w losowej kolejności (patrz SyncProgress).
        var progress = new SyncProgress<RetrievalStage>(s =>
            side.Writer.TryWrite(new StageEvent(s.Name, s.Label, s.Count)));

        RetrievalQuery Query(string text) => new()
        {
            Text = text,
            TopK = o.TopK,
            CandidatesPerPath = o.CandidatesPerPath,
            MinChunkTokens = o.MinChunkTokens,
            Progress = progress,
        };

        // ROUTER INTENCJI (Zadanie 8 planu ROU) — jedyne miejsce, w którym tura może pominąć bazę.
        // Trzy warunki muszą być spełnione JEDNOCZEŚNIE, żeby do tego doszło; każdy z nich jest
        // samodzielną linią obrony:
        //   (1) flaga włączona,               (2) wołający nie wymusił retrievalu (analiza pism!),
        //   (3) brak jawnego odwołania prawnego w wiadomości  → i dopiero wtedy pytamy model.
        // Bezpiecznik z (3) jest sprawdzany PRZED routerem także dlatego, że oszczędza wywołanie modelu.
        if (o.RouterEnabled && !forceRetrieval && router is not null &&
            !LegalTokenDetector.ContainsLegalReference(question))
        {
            yield return new StageEvent("router", "Rozpoznaję pytanie…", null);
            var decision = await router.RouteAsync(question, history, ct);
            if (!decision.PotrzebnePrzepisy)
            {
                // Ścieżka BEZ retrievalu: własny prompt, ZERO źródeł, więc świadomie bez bramki
                // abstynencji i bez walidacji cytatów — nie ma czego walidować. Dlatego prompt sam
                // pilnuje, żeby model nie zaczął tu udzielać porad prawnych z pamięci.
                yield return new NoRetrievalEvent(decision.Uzasadnienie);
                await foreach (var e in SmalltalkAsync(question, ct)) yield return e;
                yield break;
            }
        }

        // Follow-upy: dopytanie („a co z § 2?") samo embeduje się bezwartościowo, więc retrieval liczony
        // 2x (surowy vs kontekstowy) i wybór wariantu — CAŁOŚĆ w FollowUpSelector, wspólnym dla /api/chat,
        // tego serwisu i evalu. Nie kopiować tej logiki z powrotem: rozjazd kopii = rozjazd metryki.
        // TOOL CALLING (Zadanie 15) — model sam formułuje zapytanie do bazy. Jeśli je zawoła,
        // JEGO zapytanie zasila retrieval; jeśli nie (albo serwer nie wspiera `tools` i provider
        // zdegradował), lecimy dalej z pytaniem użytkownika. Brak wywołania NIGDY nie prowadzi do
        // odpowiedzi bez źródeł — po prostu wracamy do ścieżki klasycznej.
        var retrievalQuestion = question;
        if (o.ToolCallingEnabled)
        {
            yield return new StageEvent("tool_call", "Ustalam, czego szukać w przepisach…", null);
            var (toolPrompt, _) = GroundedPrompt.Build(question, [], history);
            var loop = await ToolLoop.CollectQueriesAsync(llm, toolPrompt.Messages, o.MaxToolCalls, ct);
            if (!loop.NoToolCall)
            {
                retrievalQuestion = loop.Queries[0];
                yield return new RetryingRetrievalEvent(retrievalQuestion, "zapytanie sformułowane przez model");
            }
        }

        // GapClosingRetrieval (Zadanie 12): jedno wejście retrievalu dla czatu, /api/chat i evalu.
        // Gdy runda 1 nie daje pokrycia — druga runda z przeformułowanym zapytaniem, zamiast odmowy.
        var selectionTask = GapClosingRetrieval.RetrieveAsync(
            retriever, Query, retrievalQuestion, history, o.FollowUpSignalMargin, o.RerankSignalMargin,
            o.AbstentionThreshold, o.GapClosingEnabled ? reformulator : null, o.MaxExtraRounds, ct);

        // Etapy retrievalu płyną do UI W TRAKCIE — bez tego użytkownik ma kilkadziesiąt sekund ciszy.
        // Jeden oczekujący waiter naraz (odtwarzany po każdym odczycie), żeby nie zostawiać za sobą
        // porzuconych rejestracji na kanale.
        var waitForStage = side.Reader.WaitToReadAsync(ct).AsTask();
        while (true)
        {
            var finished = await Task.WhenAny(selectionTask, waitForStage);
            while (side.Reader.TryRead(out var pending)) yield return pending;
            if (finished == (Task)selectionTask) break;
            waitForStage = side.Reader.WaitToReadAsync(ct).AsTask();
        }

        var outcome = await selectionTask;
        var (query, result) = (outcome.Query, outcome.Result);

        // Druga runda wykonana — pokazujemy CZEGO szukaliśmy. Zdarzenie leci PO fakcie (retrieval
        // jest awaitowany jako całość), ale przed źródłami, więc użytkownik widzi je przy tej turze.
        if (outcome.ExtraRound && outcome.ReformulatedQuery is { } newQuery)
            yield return new RetryingRetrievalEvent(newQuery,
                "źródła z pierwszego wyszukiwania nie domykały pytania");

        // BRAMKA ABSTYNENCJI — brak pokrycia w źródłach → nie generujemy.
        if (AbstentionPolicy.ShouldAbstain(result, o.AbstentionThreshold))
        {
            yield return new AbstainEvent(AbstentionPolicy.Message, result.MaxSimilarity);
            yield return new DoneEvent(Abstained: true, Model: null, Check: null);
            yield break;
        }

        // AKT-2/4b: oznacz źródła-nowele (niezależnie jak trafiły do wyników) + dołóż nowe fragmenty
        // dotyczące pytanych artykułów (best-effort — awaria nie blokuje odpowiedzi). Dostaje EFEKTYWNE
        // zapytanie (może być sklejone z historią) — to ono niesie cytaty z poprzednich tur.
        var chunks = result.Chunks;
        yield return new StageEvent("augment", "Sprawdzam nowelizacje…", result.Chunks.Count);
        try { chunks = await LatencyLog.TimeAsync("augment", () => augmenter.AugmentAsync(query, result.Chunks, ct)); }
        catch { /* best-effort */ }

        // Norma przed narracjami (PRZEPISY → ORZECZNICTWO) — TEN SAM porządek musi zasilić prompt,
        // panel źródeł i kontekst walidatora (jedna numeracja [n]).
        chunks = GroundedPrompt.OrderForGrounding(chunks);

        // DOC-4: fragmenty załącznika wybrane pod ORYGINALNE pytanie (in-memory cosine). Świadomie
        // PO bramce abstynencji — dokument to fakty, nie prawo; nie może zamienić odmowy w odpowiedź
        // (decyzja #5 planu DOC).
        IReadOnlyList<DocFragment> docFragments = [];
        if (_documentsEnabled && document is not null)
        {
            var qvec = await embedder.EmbedQueryAsync(question, ct);
            docFragments = document.SelectFragments(qvec);
            yield return new DocSourcesEvent(document.FileName,
                docFragments.Select(f => new DocSource(f.Index, Snippet(f.Text))).ToList());
        }

        // Do promptu idzie ORYGINALNE pytanie + historia (nie sklejony tekst retrievalu).
        var (request, sources) = GroundedPrompt.Build(question, chunks, history,
            docFragments.Select(f => f.Text).ToList());
        yield return new SourcesEvent(sources
            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet, s.AmendmentEffectiveDate, s.LegalBases)).ToList());

        // Tokeny in/out z providera (usage przychodzi na końcu strumienia) — zbierane zawsze,
        // widoczność w UI steruje flaga Diagnostics:ShowTokenUsage. Rozumowanie (thinking) — jeśli model
        // je wystawił (Gemini/Gemma) — provider oddaje je osobno, poza widocznym strumieniem.
        LlmUsage? usage = null;
        string? reasoning = null;
        request = request with
        {
            OnUsage = u => usage = u,
            OnReasoning = r => reasoning = r,
            // Rozumowanie NA ŻYWO (Zadanie 1): ~41 z ~85 s odpowiedzi. Leci tym samym kanałem co etapy,
            // bo callback providera też nie może `yield return`.
            OnReasoningDelta = d => side.Writer.TryWrite(new ReasoningDeltaEvent(d)),
        };

        // ANTY-FABRYKACJA — czy cytaty [n]/[Dk]/artykuły/sygnatury istnieją w dostarczonym kontekście.
        var contextTexts = chunks
            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var docTexts = docFragments.Select(f => f.Text).ToList();

        // WSPÓLNY BUDŻET NAPRAWCZY TURY (Zadania 10/12/13) — jeden licznik dla wszystkich mechanizmów
        // naprawczych, bo inaczej by się skumulowały: regeneracja bramki + druga runda retrievalu
        // + regeneracja po odmowie treściowej to trzy dodatkowe wywołania modelu, czyli tura licząca
        // się w minutach. `extraRoundUsed` startuje jako true, gdy GapClosingRetrieval już swoją
        // dodatkową rundę wykorzystał (Zadanie 12) — wtedy wyzwalacz treściowy nie odpala kolejnej.
        var regenerated = false;
        var extraRoundUsed = outcome.ExtraRound;
        var gateEnabled = _grounding.CitationGateEnabled;
        CitationCheck check;
        string answer;

        while (true)
        {
            yield return new StageEvent("llm", regenerated ? "Poprawiam odpowiedź…" : "Piszę odpowiedź…",
                sources.Count);

            var full = new StringBuilder();
            var llmSw = System.Diagnostics.Stopwatch.StartNew();
            var firstTokenMs = -1L;
            await foreach (var delta in llm.StreamCompletionAsync(request, ct))
            {
                // Delty rozumowania wyprzedzają widoczny tekst (model najpierw myśli) — drenaż PRZED
                // tokenem zachowuje realną kolejność zdarzeń w UI.
                while (side.Reader.TryRead(out var thought)) yield return thought;
                if (firstTokenMs < 0) firstTokenMs = llmSw.ElapsedMilliseconds;
                full.Append(delta);
                yield return new TokenEvent(delta);
            }
            // Model, który TYLKO myślał (albo domknął myślenie po ostatnim widocznym tokenie) — bez tego
            // ogona ostatnie delty rozumowania zginęłyby.
            while (side.Reader.TryRead(out var lastThought)) yield return lastThought;
            LatencyLog.Mark(regenerated ? "llm.first_token.retry" : "llm.first_token", firstTokenMs);
            LatencyLog.Mark(regenerated ? "llm.total.retry" : "llm.total", llmSw.ElapsedMilliseconds);

            answer = full.ToString();
            check = CitationValidator.Validate(answer, contextTexts, sources.Count, docTexts, docFragments.Count);

            // WYZWALACZ TREŚCIOWY (Zadanie 13) — WAŻNIEJSZY z dwóch wyzwalaczy pętli domykającej.
            // Bramka abstynencji patrzy na sygnał retrievalu, ale odmowa z reguły 3 promptu żyje
            // w TREŚCI odpowiedzi: model dostał źródła ponad progiem i sam orzekł, że nie odpowiadają
            // na pytanie. Ten przypadek jest u nas normą, nie wyjątkiem — odmowy są treściowe,
            // nie progowe — a bez tego wyzwalacza pętla domykająca w ogóle by go nie widziała.
            if (o.GapClosingEnabled && !extraRoundUsed && o.MaxExtraRounds > 0 && reformulator is not null
                && answer.Contains(AbstentionPolicy.Message, StringComparison.Ordinal))
            {
                var retryQuery = await reformulator.ReformulateAsync(question, ct);
                if (retryQuery is not null)
                {
                    extraRoundUsed = true;
                    yield return new RetryingRetrievalEvent(retryQuery,
                        "model uznał, że źródła nie odpowiadają na pytanie");

                    var retryOutcome = await GapClosingRetrieval.RetrieveAsync(
                        retriever, Query, retryQuery, history, o.FollowUpSignalMargin, o.RerankSignalMargin,
                        o.AbstentionThreshold, reformulator: null, maxExtraRounds: 0, ct);
                    while (side.Reader.TryRead(out var stage)) yield return stage;

                    // Nowe źródła TYLKO jeśli faktycznie mają pokrycie — inaczej druga próba
                    // generowałaby na gorszym kontekście niż pierwsza.
                    if (!AbstentionPolicy.ShouldAbstain(retryOutcome.Result, o.AbstentionThreshold))
                    {
                        chunks = GroundedPrompt.OrderForGrounding(retryOutcome.Result.Chunks);
                        (request, sources) = GroundedPrompt.Build(question, chunks, history, docTexts);
                        contextTexts = chunks
                            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
                        request = request with
                        {
                            OnUsage = u => usage = u,
                            OnReasoning = r => reasoning = r,
                            OnReasoningDelta = d => side.Writer.TryWrite(new ReasoningDeltaEvent(d)),
                        };
                        yield return new SourcesEvent(sources
                            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet,
                                s.AmendmentEffectiveDate, s.LegalBases)).ToList());
                        continue; // druga generacja, na NOWYM kontekście
                    }
                }
            }

            if (!gateEnabled) break; // flaga OFF = dzisiejsze zachowanie (badge, odpowiedź wychodzi)

            var decision = AnswerGate.Decide(check, alreadyRegenerated: regenerated);
            if (decision.Verdict == AnswerVerdict.Pass) break;

            if (decision.Verdict == AnswerVerdict.Refuse)
            {
                // Druga próba też cytuje coś, czego nie ma w źródłach — NIE wypuszczamy.
                if (!string.IsNullOrWhiteSpace(reasoning)) yield return new ReasoningEvent(reasoning);
                yield return new AbstainEvent(decision.Text, result.MaxSimilarity);
                LatencyLog.Mark("chat.total", chatSw.ElapsedMilliseconds);
                yield return new DoneEvent(Abstained: true, Model: llm.ModelId, Check: check, Usage: usage);
                yield break;
            }

            // Regeneracja na TYM SAMYM kontekście — dodatkowa runda retrievalu to Zadanie 12, nie tutaj.
            regenerated = true;
            yield return new RegeneratingEvent(decision.Text);
            request = request with
            {
                Messages = [.. request.Messages, new ChatMessage(ChatRole.User, decision.Text)],
            };
        }

        if (!string.IsNullOrWhiteSpace(reasoning))
            yield return new ReasoningEvent(reasoning);

        LatencyLog.Mark("chat.total", chatSw.ElapsedMilliseconds);
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: check, Usage: usage,
            Regenerated: regenerated);
    }

    /// <summary>
    /// Ścieżka BEZ retrievalu (router orzekł, że przepisy nie są potrzebne). Świadomie NIE przechodzi
    /// przez <see cref="GroundedPrompt"/>, bramkę abstynencji ani <see cref="CitationValidator"/> —
    /// przy zerowej liczbie źródeł te mechanizmy nie mają na czym pracować (reguły cytowania [n]
    /// kazałyby modelowi cytować coś, czego nie ma). Zamiast nich pilnuje tego
    /// <see cref="SmalltalkPrompt"/>, tematycznie zamknięty.
    ///
    /// Niski limit tokenów: reguła R2 planu — rozumowanie tylko przy PISANIU odpowiedzi na źródłach,
    /// a nie przy „siema". Odpowiedź ma paść w ~2 s, nie po 40 s myślenia.
    /// </summary>
    private async IAsyncEnumerable<ChatEvent> SmalltalkAsync(
        string question, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new LlmRequest
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, SmalltalkPrompt.SystemPrompt),
                new ChatMessage(ChatRole.User, question),
            ],
            Temperature = 0,
            MaxTokens = 256,
        };

        LlmUsage? usage = null;
        request = request with { OnUsage = u => usage = u };

        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
            yield return new TokenEvent(delta);

        // Check = null: nie ma źródeł, więc nie ma czego walidować. UI po tym (i po NoRetrievalEvent)
        // wie, że nie może pokazać badge'a „cytaty zgodne" — bo nie było cytatów.
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: null, Usage: usage);
    }

    private static string Snippet(string text, int max = 300) =>
        text.Length <= max ? text : text[..max] + "…";
}
