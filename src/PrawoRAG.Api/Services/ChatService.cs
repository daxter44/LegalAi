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
public sealed class ChatService(IRetriever retriever, ILlmProvider llm, IOptions<RetrievalOptions> options) : IChatService
{
    public async IAsyncEnumerable<ChatEvent> AskAsync(string question, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        var query = new RetrievalQuery
        {
            Text = question,
            TopK = o.TopK,
            CandidatesPerPath = o.CandidatesPerPath,
            MinChunkTokens = o.MinChunkTokens,
        };

        var result = await retriever.RetrieveAsync(query, ct);

        // BRAMKA ABSTYNENCJI — brak pokrycia w źródłach → nie generujemy.
        if (AbstentionPolicy.ShouldAbstain(result, o.AbstentionThreshold))
        {
            yield return new AbstainEvent(AbstentionPolicy.Message, result.MaxSimilarity);
            yield return new DoneEvent(Abstained: true, Model: null, Check: null);
            yield break;
        }

        var (request, sources) = GroundedPrompt.Build(question, result.Chunks);
        yield return new SourcesEvent(sources
            .Select(s => new ChatSource(s.Index, s.Label, s.Title, s.SourceUrl, s.Snippet)).ToList());

        var full = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            full.Append(delta);
            yield return new TokenEvent(delta);
        }

        // ANTY-FABRYKACJA — czy cytaty [n]/artykuły/sygnatury istnieją w dostarczonym kontekście.
        var contextTexts = result.Chunks
            .Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count);
        yield return new DoneEvent(Abstained: false, Model: llm.ModelId, Check: check);
    }
}
