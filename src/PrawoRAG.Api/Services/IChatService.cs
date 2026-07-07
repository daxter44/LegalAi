namespace PrawoRAG.Api.Services;

/// <summary>
/// Fasada czatu dla UI: opakowuje retrieval → bramkę abstynencji → ugruntowany prompt → streaming LLM →
/// kontrolę cytatów, oddając strumień <see cref="ChatEvent"/>. UI nie zna szczegółów RAG.
/// </summary>
public interface IChatService
{
    IAsyncEnumerable<ChatEvent> AskAsync(string question, CancellationToken ct);
}
