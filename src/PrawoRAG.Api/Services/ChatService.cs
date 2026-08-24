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
    IIntentRouter? router = null) : IChatService
{
    private readonly bool _documentsEnabled = documents.Value.Enabled;

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
        var selectionTask = FollowUpSelector.SelectAsync(
            retriever, Query, question, history, o.FollowUpSignalMargin, o.RerankSignalMargin, ct);

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

        var selection = await selectionTask;
        var (query, result) = (selection.Query, selection.Result);

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

        yield return new StageEvent("llm", "Piszę odpowiedź…", sources.Count);

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
        LatencyLog.Mark("llm.first_token", firstTokenMs);
        LatencyLog.Mark("llm.total", llmSw.ElapsedMilliseconds);

        if (!string.IsNullOrWhiteSpace(reasoning))
            yield return new ReasoningEvent(reasoning);

        // ANTY-FABRYKACJA — czy cytaty [n]/[Dk]/artykuły/sygnatury istnieją w dostarczonym kontekście.
        var contextTexts = chunks
            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count,
            docFragments.Select(f => f.Text).ToList(), docFragments.Count);
        LatencyLog.Mark("chat.total", chatSw.ElapsedMilliseconds);
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: check, Usage: usage);
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
