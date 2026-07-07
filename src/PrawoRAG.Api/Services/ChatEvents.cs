using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>Źródło do panelu obok odpowiedzi (numerowane [n], z dosłownym cytatem i linkiem do oryginału).</summary>
public sealed record ChatSource(int Index, string Label, string Title, string? Url, string Snippet);

/// <summary>
/// Zdarzenia strumienia czatu (in-process, odpowiednik zdarzeń SSE z /api/chat). Kolejność:
/// (Abstain) | (Sources → Token* → Done). Error może wystąpić w dowolnym momencie.
/// </summary>
public abstract record ChatEvent;

/// <summary>Retrieval zwrócił źródła — pokazujemy je PRZED generacją (transparentność).</summary>
public sealed record SourcesEvent(IReadOnlyList<ChatSource> Sources) : ChatEvent;

/// <summary>Kolejny fragment odpowiedzi LLM (streaming token po tokenie).</summary>
public sealed record TokenEvent(string Text) : ChatEvent;

/// <summary>Bramka abstynencji: brak pokrycia w źródłach — nie generujemy odpowiedzi.</summary>
public sealed record AbstainEvent(string Message, double MaxSimilarity) : ChatEvent;

/// <summary>Koniec: wynik kontroli anty-fabrykacji (cytaty) + model.</summary>
public sealed record DoneEvent(bool Abstained, string? Model, CitationCheck? Check) : ChatEvent;

/// <summary>Błąd przetwarzania.</summary>
public sealed record ChatErrorEvent(string Message) : ChatEvent;
