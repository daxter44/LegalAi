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
    IAsyncEnumerable<ChatEvent> AskAsync(string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
        => AskAsync(question, history, document: null, ct);

    /// <summary>Wariant z załącznikiem (DOC-4): <paramref name="document"/> = przetworzony PDF
    /// użytkownika (fakty, przestrzeń [Dk]); null = zachowanie jak dotąd.</summary>
    IAsyncEnumerable<ChatEvent> AskAsync(
        string question, IReadOnlyList<ChatTurn> history, DocumentContext? document, CancellationToken ct)
        => AskAsync(question, history, document, forceRetrieval: false, ct);

    /// <summary>
    /// Wariant z wymuszonym retrievalem (Zadanie 8 planu ROU). <paramref name="forceRetrieval"/>=true
    /// POMIJA router intencji i bezpiecznik — retrieval jest bezwarunkowy, jak przed Fazą 2.
    ///
    /// Kto tego potrzebuje: <see cref="AnalysisRunner"/>, który woła ten sam <c>AskAsync</c> per
    /// jednostkę analizowanego dokumentu. Jednostka bez tokenu prawnego (preambuła, komparycja, dane
    /// adresowe stron) plus pomyłka routera dałaby WERDYKT ANALIZY bez retrievalu, czyli nieugruntowany —
    /// w dokumencie, który wygląda na audytowy. Jednostka map-reduce nigdy nie jest small-talkiem,
    /// więc pytanie o to routera jest bezcelowe, a ryzykowne.
    /// </summary>
    IAsyncEnumerable<ChatEvent> AskAsync(
        string question, IReadOnlyList<ChatTurn> history, DocumentContext? document,
        bool forceRetrieval, CancellationToken ct);
}
