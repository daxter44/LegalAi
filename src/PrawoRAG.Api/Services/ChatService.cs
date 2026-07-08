using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Implementacja fasady czatu — ta sama logika co endpoint SSE /api/chat, ale in-process i jako strumień
/// <see cref="ChatEvent"/> dla Blazora. Rdzeń wartości (abstynencja + anty-fabrykacja) zostaje tu, nie w UI.
/// </summary>
public sealed class ChatService(
    IRetriever retriever, ITemporalAugmenter augmenter, ILlmProvider llm, IOptions<RetrievalOptions> options) : IChatService
{
    public async IAsyncEnumerable<ChatEvent> AskAsync(
        string question, IReadOnlyList<ChatTurn> history, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        RetrievalQuery Query(string text) => new()
        {
            Text = text,
            TopK = o.TopK,
            CandidatesPerPath = o.CandidatesPerPath,
            MinChunkTokens = o.MinChunkTokens,
        };

        // Follow-upy: dopytanie („a co z § 2?") samo embeduje się bezwartościowo. Retrieval liczony 2x —
        // (a) samo pytanie, (b) pytanie + poprzednie pytania użytkownika sklejone. Wybór ASYMETRYCZNY
        // z marginesem (FollowUpQuery.PickContextual): różnice sygnału bywają szumem rzędu 1e-6, a koszt
        // fałszywego surowego (śmieciowe źródła) >> koszt fałszywego kontekstowego. SEKWENCYJNIE (wspólny
        // scoped DbContext nie jest thread-safe). Sklejony tekst niesie cytaty z historii („art. 367 KPC")
        // → retrieval strukturalny (QU) i augmenter działają na follow-upach.
        var query = Query(question);
        var result = await retriever.RetrieveAsync(query, ct);
        if (history.Count > 0)
        {
            var ctxText = FollowUpQuery.Contextualize(history.Select(t => t.Question).ToList(), question);
            var ctxQuery = Query(ctxText);
            var ctxResult = await retriever.RetrieveAsync(ctxQuery, ct);
            if (FollowUpQuery.PickContextual(result.MaxSimilarity, ctxResult.MaxSimilarity, o.FollowUpSignalMargin))
                (query, result) = (ctxQuery, ctxResult);
        }

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
        try { chunks = await augmenter.AugmentAsync(query, result.Chunks, ct); } catch { /* best-effort */ }

        // Do promptu idzie ORYGINALNE pytanie + historia (nie sklejony tekst retrievalu).
        var (request, sources) = GroundedPrompt.Build(question, chunks, history);
        yield return new SourcesEvent(sources
            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet, s.AmendmentEffectiveDate)).ToList());

        var full = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            full.Append(delta);
            yield return new TokenEvent(delta);
        }

        // ANTY-FABRYKACJA — czy cytaty [n]/artykuły/sygnatury istnieją w dostarczonym kontekście.
        var contextTexts = chunks
            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count);
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: check);
    }
}
