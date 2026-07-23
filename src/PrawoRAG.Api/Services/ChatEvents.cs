using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>Źródło do panelu obok odpowiedzi (numerowane [n], z dosłownym cytatem i linkiem do oryginału).
/// AKT-4: <see cref="AmendmentEffectiveDate"/> niepuste ⇔ fragment nowelizacji niewchłoniętej do t.j.</summary>
public sealed record ChatSource(int Index, string Label, string Title, string? Url, string Snippet, string? AmendmentEffectiveDate = null);

/// <summary>
/// Zdarzenia strumienia czatu (in-process, odpowiednik zdarzeń SSE z /api/chat). Kolejność:
/// (Abstain) | (Sources → Token* → Done). Error może wystąpić w dowolnym momencie.
/// </summary>
public abstract record ChatEvent;

/// <summary>Retrieval zwrócił źródła — pokazujemy je PRZED generacją (transparentność).</summary>
public sealed record SourcesEvent(IReadOnlyList<ChatSource> Sources) : ChatEvent;

/// <summary>Fragment załącznika wybrany do promptu (przestrzeń [Dk], DOC-4) — do panelu „Twój dokument".</summary>
public sealed record DocSource(int Index, string Snippet);

/// <summary>Fragmenty załącznika użyte w tej turze — emitowane PRZED generacją, obok SourcesEvent.</summary>
public sealed record DocSourcesEvent(string FileName, IReadOnlyList<DocSource> Fragments) : ChatEvent;

/// <summary>Kolejny fragment odpowiedzi LLM (streaming token po tokenie).</summary>
public sealed record TokenEvent(string Text) : ChatEvent;

/// <summary>„Rozumowanie" modelu (thinking/CoT) wydzielone ze strumienia — emitowane RAZ, po tokenach,
/// przed <see cref="DoneEvent"/>. UI pokazuje je w rozwijanej sekcji (jak źródła). Puste/brak = model
/// nie „myślał" (Claude/Bielik) → event nie leci.</summary>
public sealed record ReasoningEvent(string Text) : ChatEvent;

/// <summary>Bramka abstynencji: brak pokrycia w źródłach — nie generujemy odpowiedzi.</summary>
public sealed record AbstainEvent(string Message, double MaxSimilarity) : ChatEvent;

/// <summary>Koniec: wynik kontroli anty-fabrykacji (cytaty) + model. <see cref="Usage"/> = tokeny
/// in/out z providera (zbierane zawsze; widoczność w UI steruje flaga Diagnostics:ShowTokenUsage).</summary>
public sealed record DoneEvent(bool Abstained, string? Model, CitationCheck? Check, LlmUsage? Usage = null) : ChatEvent;

/// <summary>Błąd przetwarzania.</summary>
public sealed record ChatErrorEvent(string Message) : ChatEvent;
