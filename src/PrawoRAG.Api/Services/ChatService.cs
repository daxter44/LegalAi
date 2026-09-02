using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    IQueryReformulator? reformulator = null,
    ILogger<ChatService>? logger = null) : IChatService
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
            // Rozszerzenie sąsiedztwa artykułów (plan SAS) — dotyczy CZATU, bo tu model musi znaleźć
            // przepis pod nazwą, której użytkownik nie zna. /api/search zostaje bez tego: tam wynikiem
            // jest lista trafień do przejrzenia, nie kontekst dla modelu.
            NeighbourhoodRadius = o.NeighbourhoodRadius,
            NeighbourhoodMinChunks = o.NeighbourhoodMinChunks,
            NeighbourhoodTokenBudget = o.NeighbourhoodTokenBudget,
        };

        // PROŚBA O SPORZĄDZENIE DOKUMENTU (Horyzont 0 draftingu, rozmowa 2026-08-28): system nie
        // przygotowuje pism — zamiast niezdefiniowanego zachowania odpowiadamy wymogami prawnymi
        // dokumentu ze źródłami (doklejka DraftingRules do promptu, niżej). Wykrycie WYMUSZA
        // retrieval (wymogi są w przepisach), więc router nie jest wołany. Log = licznik do bety:
        // skala takich próśb to sygnał produktowy pod Horyzont 1 (generowanie prostych pism).
        var draftingRequest = DraftingRequestDetector.IsDraftingRequest(question);
        if (draftingRequest)
        {
            logger?.LogInformation("DRAFTING_REQUEST: {Question}", question);
            yield return new StageEvent("drafting",
                "Prośba o dokument — odpowiem wymogami i podstawami prawnymi…", null);
        }

        // ROUTER INTENCJI (Zadanie 8 planu ROU) — jedyne miejsce, w którym tura może pominąć bazę.
        // Cztery warunki muszą być spełnione JEDNOCZEŚNIE, żeby do tego doszło; każdy z nich jest
        // samodzielną linią obrony:
        //   (1) flaga włączona,               (2) wołający nie wymusił retrievalu (analiza pism!),
        //   (3) brak jawnego odwołania prawnego w wiadomości,
        //   (4) to nie prośba o dokument      → i dopiero wtedy pytamy model.
        // Bezpieczniki (3)/(4) są sprawdzane PRZED routerem także dlatego, że oszczędzają wywołanie modelu.
        if (o.RouterEnabled && !forceRetrieval && router is not null && !draftingRequest &&
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
                await foreach (var e in SmalltalkAsync(question, history, ct)) yield return e;
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
        // Historia DLA RETRIEVALU, osobno od historii dla promptu (ta zostaje pełna zawsze). Sklejka
        // kontekstowa w FollowUpSelector istnieje TYLKO dlatego, że surowe dopytanie nie niesie
        // treści — a zapytanie napisane przez model, który widział rozmowę, już ją niesie.
        var retrievalHistory = history;
        if (o.ToolCallingEnabled)
        {
            yield return new StageEvent("tool_call", "Ustalam, czego szukać w przepisach…", null);
            var (toolPrompt, _) = GroundedPrompt.Build(question, [], history);
            var loop = await ToolLoop.CollectQueriesAsync(llm, toolPrompt.Messages, o.MaxToolCalls, ct);
            if (!loop.NoToolCall)
            {
                retrievalQuestion = loop.Queries[0];
                // Zapytanie modelu jest SAMODZIELNE (prompt narzędziowy zawierał historię — wyżej),
                // więc pusta historia dla retrievalu: jeden przebieg zamiast dwóch. Wcześniej
                // sklejaliśmy je z poprzednimi pytaniami i foldem odpowiedzi, płacąc podwójny
                // embedding + SQL + reranker za pogorszenie tekstu, który model właśnie napisał.
                // Brak wywołania narzędzia → historia zostaje, czyli dzisiejsza ścieżka bez zmian.
                retrievalHistory = [];
                yield return new RetryingRetrievalEvent(retrievalQuestion, "zapytanie sformułowane przez model");
            }
        }

        // GapClosingRetrieval (Zadanie 12): jedno wejście retrievalu dla czatu, /api/chat i evalu.
        // Gdy runda 1 nie daje pokrycia — druga runda z przeformułowanym zapytaniem, zamiast odmowy.
        // UWAGA: reformulator w środku dostaje PEŁNĄ `history` (rozwiązanie odwołania), nie
        // `retrievalHistory` — te dwie rzeczy służą do czego innego.
        var selectionTask = GapClosingRetrieval.RetrieveAsync(
            retriever, Query, retrievalQuestion, retrievalHistory, o.FollowUpSignalMargin, o.RerankSignalMargin,
            o.GapClosingTriggerThreshold, o.GapClosingEnabled ? reformulator : null, o.MaxExtraRounds, ct);

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
            docFragments.Select(f => f.Text).ToList(), draftingRequest);
        yield return new SourcesEvent(sources
            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet, s.AmendmentEffectiveDate, s.LegalBases, s.Neighbour)).ToList());

        // Tokeny in/out z providera (usage przychodzi na końcu strumienia) — zbierane zawsze,
        // widoczność w UI steruje flaga Diagnostics:ShowTokenUsage. Rozumowanie (thinking) — jeśli model
        // je wystawił (Gemini/Gemma) — provider oddaje je osobno, poza widocznym strumieniem.
        LlmUsage? usage = null;
        string? reasoning = null;
        // Rozumowanie NA ŻYWO (Zadanie 1; naprawa 2026-09-02): callback pisze do kanału (nie może
        // `yield return`), a pętla generacji niżej czyta kanał RÓWNOLEGLE ze strumieniem LLM (pompa) —
        // bez tego podczas myślenia provider nie emituje ŻADNEJ widocznej delty, konsument wisi na
        // MoveNextAsync i delty rozumowania czekają w kanale aż do pierwszego tokenu (obserwacja
        // produkcyjna: „🧠 myśli…" pojawiało się dopiero razem z tekstem, po 30-90 s ciszy).
        // Pierwsza delta rozumowania przełącza też pasek etapu na „Myślę…" i stempluje czas
        // (llm.first_reasoning) — bez tego llm.first_token zlepia czytanie promptu z myśleniem.
        var thinking = false;      // model myśli w bieżącej próbie (reset na starcie każdej generacji)
        var firstReasoningTs = 0L; // Stopwatch.GetTimestamp() pierwszej delty rozumowania
        Action<string> onReasoningDelta = d =>
        {
            if (!thinking)
            {
                thinking = true;
                firstReasoningTs = System.Diagnostics.Stopwatch.GetTimestamp();
                side.Writer.TryWrite(new StageEvent("llm", "Myślę…"));
            }
            side.Writer.TryWrite(new ReasoningDeltaEvent(d));
        };
        request = request with
        {
            OnUsage = u => usage = u,
            OnReasoning = r => reasoning = r,
            OnReasoningDelta = onReasoningDelta,
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

        // US-2.11 (AI Act art. 50 ust. 2): oznaczenie pochodzenia PRZED pierwszym tokenem, RAZ na turę
        // (pętla naprawcza niżej nie emituje drugiego — to wciąż ta sama generowana odpowiedź).
        yield return new ProvenanceEvent(
            AiGenerated: true, Model: llm.ModelId, System: SystemId,
            GeneratedAt: DateTimeOffset.UtcNow, Grounded: true);

        while (true)
        {
            yield return new StageEvent("llm", regenerated ? "Poprawiam odpowiedź…" : "Piszę odpowiedź…",
                sources.Count);

            var full = new StringBuilder();
            var llmSw = System.Diagnostics.Stopwatch.StartNew();
            var llmStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            var firstTokenMs = -1L;
            thinking = false;
            firstReasoningTs = 0;
            // POMPA (naprawa 2026-09-02): strumień LLM pisze do TEGO SAMEGO kanału co delty
            // rozumowania — jeden sekwencyjny producent (callback odpala wewnątrz iteracji strumienia)
            // zachowuje prawdziwą kolejność zdarzeń (myślenie przed tekstem), a konsument niżej budzi
            // się na KAŻDE zdarzenie, nie tylko na widoczne tokeny. Wzorzec jak przy etapach
            // retrievalu wyżej (Task.WhenAny + jeden oczekujący waiter naraz).
            var pumpRequest = request;
            var pump = Task.Run(async () =>
            {
                await foreach (var delta in llm.StreamCompletionAsync(pumpRequest, ct))
                {
                    if (firstTokenMs < 0)
                    {
                        firstTokenMs = llmSw.ElapsedMilliseconds;
                        // Koniec myślenia — pasek etapu wraca z „Myślę…" do pisania odpowiedzi.
                        if (thinking) side.Writer.TryWrite(new StageEvent("llm",
                            regenerated ? "Poprawiam odpowiedź…" : "Piszę odpowiedź…", sources.Count));
                    }
                    full.Append(delta);
                    side.Writer.TryWrite(new TokenEvent(delta));
                }
            }, ct);

            var waitLlm = side.Reader.WaitToReadAsync(ct).AsTask();
            while (true)
            {
                var finishedLlm = await Task.WhenAny(pump, waitLlm);
                while (side.Reader.TryRead(out var ev)) yield return ev;
                if (finishedLlm == pump) break;
                await waitLlm; // propagacja anulowania; normalnie: dane już zdrenowane wyżej
                waitLlm = side.Reader.WaitToReadAsync(ct).AsTask();
            }
            await pump; // propagacja błędu strumienia (transport, 500 z API) — jak przy await foreach
            // Model, który TYLKO myślał (albo domknął myślenie po ostatnim widocznym tokenie) — bez tego
            // ogona ostatnie delty rozumowania zginęłyby.
            while (side.Reader.TryRead(out var lastThought)) yield return lastThought;
            if (thinking)
                LatencyLog.Mark(regenerated ? "llm.first_reasoning.retry" : "llm.first_reasoning",
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(llmStartTs, firstReasoningTs).TotalMilliseconds);
            LatencyLog.Mark(regenerated ? "llm.first_token.retry" : "llm.first_token", firstTokenMs);
            LatencyLog.Mark(regenerated ? "llm.total.retry" : "llm.total", llmSw.ElapsedMilliseconds);

            answer = full.ToString();
            check = CitationValidator.Validate(answer, contextTexts, sources.Count, docTexts, docFragments.Count);

            // WYZWALACZ TREŚCIOWY (Zadanie 13) — WAŻNIEJSZY z dwóch wyzwalaczy pętli domykającej.
            // Bramka abstynencji patrzy na sygnał retrievalu, ale odmowa z reguły 3 promptu żyje
            // w TREŚCI odpowiedzi: model dostał źródła ponad progiem i sam orzekł, że nie odpowiadają
            // na pytanie. Ten przypadek jest u nas normą, nie wyjątkiem — odmowy są treściowe,
            // nie progowe — a bez tego wyzwalacza pętla domykająca w ogóle by go nie widziała.
            // UWAGA na frazę (fix 2026-08-31): reguła 3 każe modelowi pisać TYLKO frazę odmowy
            // (GroundedPrompt.RefusalMarker) — porównanie z pełnym AbstentionPolicy.Message
            // (z doklejką „Zawęź pytanie…", której model nie zna) nigdy nie trafiało, więc wyzwalacz
            // był martwy na realnych odmowach (potwierdzone trzema diagnozami produkcyjnymi:
            // OKI, zaświadczenie/oświadczenie, znak wodny AI Act). Marker, nie pełny komunikat.
            if (o.GapClosingEnabled && !extraRoundUsed && o.MaxExtraRounds > 0 && reformulator is not null
                && answer.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase))
            {
                var retryQuery = await reformulator.ReformulateAsync(question, history, ct);
                if (retryQuery is not null)
                {
                    extraRoundUsed = true;
                    yield return new RetryingRetrievalEvent(retryQuery,
                        "model uznał, że źródła nie odpowiadają na pytanie");

                    // maxExtraRounds: 0 => próg poniżej i tak nie jest tu sprawdzany (wczesny return
                    // w GapClosingRetrieval), ale przekazujemy właściwy semantycznie parametr, nie
                    // próg odmowy — na wypadek gdyby ta wartość kiedyś przestała być 0.
                    var retryOutcome = await GapClosingRetrieval.RetrieveAsync(
                        retriever, Query, retryQuery, [], o.FollowUpSignalMargin, o.RerankSignalMargin,
                        o.GapClosingTriggerThreshold, reformulator: null, maxExtraRounds: 0, ct);
                    while (side.Reader.TryRead(out var stage)) yield return stage;

                    // Nowe źródła TYLKO jeśli faktycznie mają pokrycie — inaczej druga próba
                    // generowałaby na gorszym kontekście niż pierwsza.
                    if (!AbstentionPolicy.ShouldAbstain(retryOutcome.Result, o.AbstentionThreshold))
                    {
                        // Lustro rundy 1 (diagnoza 2026-09-01): druga runda POMIJAŁA TemporalAugmenter,
                        // więc nowele wracały bez markera [NOWELIZACJA] dokładnie tam, gdzie pomoc jest
                        // najpotrzebniejsza (pierwsza generacja już poległa na dwóch wersjach przepisu).
                        // Best-effort jak w rundzie 1; PRZED OrderForGrounding, żeby dołożone nowele
                        // weszły w porządek PRZEPISY→ORZECZNICTWO i numerację [n] walidatora.
                        var retryChunks = retryOutcome.Result.Chunks;
                        yield return new StageEvent("augment", "Sprawdzam nowelizacje…", retryChunks.Count);
                        try
                        {
                            retryChunks = await LatencyLog.TimeAsync("augment.retry",
                                () => augmenter.AugmentAsync(query, retryChunks, ct));
                        }
                        catch { /* best-effort */ }

                        chunks = GroundedPrompt.OrderForGrounding(retryChunks);
                        (request, sources) = GroundedPrompt.Build(question, chunks, history, docTexts);
                        contextTexts = chunks
                            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
                        request = request with
                        {
                            OnUsage = u => usage = u,
                            OnReasoning = r => reasoning = r,
                            OnReasoningDelta = onReasoningDelta,
                        };
                        yield return new SourcesEvent(sources
                            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet,
                                s.AmendmentEffectiveDate, s.LegalBases, s.Neighbour)).ToList());
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
        // WARIANT A telemetrii (2026-08-31): Abstained znaczy „użytkownik nie dostał odpowiedzi
        // merytorycznej" niezależnie od mechanizmu. Odmowa treściowa (reguła 3 promptu — u nas
        // NORMA, bo AbstentionThreshold=0.0 usypia bramkę progową od czasu znaleziska o sygnale
        // rerankera) kończyła się dotąd Abstained=false, więc metryka nadrzędna (odsetek odmów)
        // liczona z kolumny messages.Abstained pokazywała ~0% mimo realnych odmów.
        // ODM-4: kanoniczna definicja odmowy treściowej (fraza BEZ cytowań [n]) — odpowiedź mieszana
        // (model złamał regułę 3: fraza + treść z [n]) to nie odmowa, użytkownik dostał odpowiedź.
        yield return new DoneEvent(
            Abstained: GroundedPrompt.IsContentRefusal(answer),
            Model: llm.ModelId, Check: check, Usage: usage, Regenerated: regenerated);
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
    ///
    /// HISTORIA (2026-08-27): ta ścieżka dostaje poprzednie tury. Bez nich router, który orzekł
    /// „przepisy niepotrzebne" dla „streść to krócej" albo „rozwiń ostatni punkt", wysyłał model
    /// z SAMĄ tą wiadomością — czyli bez czegokolwiek do streszczenia. Model nie miał wtedy żadnego
    /// dobrego wyjścia: albo pytał „co mam streścić?", albo dorabiał treść z pamięci parametrycznej,
    /// a ta ścieżka nie ma ani bramki abstynencji, ani walidacji cytatów, żeby to wyłapać.
    /// Zakres tego, co model może z historią zrobić, pilnuje <see cref="SmalltalkPrompt"/>
    /// (przeredagowanie TAK, dokładanie nowej treści prawnej NIE).
    /// </summary>
    private async IAsyncEnumerable<ChatEvent> SmalltalkAsync(
        string question, IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
    {
        // Ta sama historia i ta sama sanityzacja co w GroundedPrompt (markery [n] zdjęte: odnosiły się
        // do źródeł TAMTEJ tury, a tu nie ma żadnych źródeł, więc nie mogłyby na nic wskazywać).
        var messages = new List<ChatMessage> { new(ChatRole.System, SmalltalkPrompt.SystemPrompt) };
        GroundedPrompt.AppendHistory(messages, history);
        // Scalanie roli, nie zwykłe Add: gdy ostatnia tura skończyła się abstynencją (Answer=null),
        // historia kończy się wiadomością User i dwie z rzędu łamią naprzemienność.
        GroundedPrompt.AddCoalescing(messages, new ChatMessage(ChatRole.User, question));

        var request = new LlmRequest
        {
            Messages = messages,
            Temperature = 0,
            // 512, nie 256: przeredagowanie poprzedniej odpowiedzi („w punktach") potrzebuje miejsca
            // na treść, a nie na jedno zdanie. Nadal rząd wielkości poniżej odpowiedzi na źródłach —
            // reguła R2 (bez rozumowania na tej ścieżce) zostaje.
            MaxTokens = 512,
        };

        LlmUsage? usage = null;
        request = request with { OnUsage = u => usage = u };

        // US-2.11: oznaczenie pochodzenia także na ścieżce bez retrievalu (Grounded=false).
        yield return new ProvenanceEvent(
            AiGenerated: true, Model: llm.ModelId, System: SystemId,
            GeneratedAt: DateTimeOffset.UtcNow, Grounded: false);

        var emptyOutput = true;
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            if (emptyOutput && !string.IsNullOrWhiteSpace(delta)) emptyOutput = false;
            yield return new TokenEvent(delta);
        }

        // ODM-1: model na tej ścieżce potrafił zwrócić PUSTY strumień (złapane żywcem 2026-09-01 na
        // „przepis na zupę pomidorową"). Reguła 6 SmalltalkPrompt to pierwsza linia, ale to prośba —
        // gwarancję daje serwer: pusta odpowiedź dostaje standardowe zdanie odmowy TUTAJ, więc trafia
        // do bazy, historii follow-upów, czatu, /api/chat i dopytań analizy tak samo.
        if (emptyOutput)
            yield return new TokenEvent(SmalltalkPrompt.OutOfScopeMessage);

        // Check = null: nie ma źródeł, więc nie ma czego walidować. UI po tym (i po NoRetrievalEvent)
        // wie, że nie może pokazać badge'a „cytaty zgodne" — bo nie było cytatów.
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: null, Usage: usage);
    }

    /// <summary>Identyfikator naszego SYSTEMU (nie modelu) do oznaczenia pochodzenia — AI Act
    /// art. 50 ust. 2 adresuje dostawcę systemu, więc oznaczenie mówi „kto wygenerował", nie tylko
    /// „którym modelem".</summary>
    internal static readonly string SystemId =
        $"OmniaSI/{typeof(ChatService).Assembly.GetName().Version?.ToString(3) ?? "dev"}";

    private static string Snippet(string text, int max = 300) =>
        text.Length <= max ? text : text[..max] + "…";
}
