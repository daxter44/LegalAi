using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Fasada czatu dla UI: opakowuje retrieval → bramkę abstynencji → ugruntowany prompt → streaming LLM →
/// kontrolę cytatów, oddając strumień <see cref="ChatEvent"/>. UI nie zna szczegółów RAG.
/// <paramref name="history"/> = poprzednie zakończone tury rozmowy (kontekst follow-upów); pusta lista
/// = zachowanie jednoturowe jak dotąd.
/// </summary>
public interface IChatService
{
    IAsyncEnumerable<ChatEvent> AskAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct);
}
