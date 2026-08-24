using PrawoRAG.Domain.Llm;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>Źródło do panelu obok odpowiedzi (numerowane [n], z dosłownym cytatem i linkiem do oryginału).
/// AKT-4: <see cref="AmendmentEffectiveDate"/> niepuste ⇔ fragment nowelizacji niewchłoniętej do t.j.</summary>
public sealed record ChatSource(int Index, string Label, string Title, string? Url, string Snippet, string? AmendmentEffectiveDate = null, IReadOnlyList<string>? LegalBases = null);

/// <summary>
/// Zdarzenia strumienia czatu (in-process, odpowiednik zdarzeń SSE z /api/chat). Kolejność:
/// (Stage* → Abstain) | (Stage* → Sources → (Token | ReasoningDelta)* → Done).
/// Error może wystąpić w dowolnym momencie.
/// </summary>
public abstract record ChatEvent;

/// <summary>
/// Etap pracy systemu (Zadanie 2/3 planu ROU) — retrieval trwa dziesiątki sekund i bez tego UI
/// nie miało czym pokazać, że coś się dzieje. <see cref="Stage"/> to nazwa techniczna zgodna
/// z <c>LatencyLog</c> (dla diagnostyki), <see cref="Label"/> to tekst dla użytkownika.
/// </summary>
public sealed record StageEvent(string Stage, string Label, int? Count = null) : ChatEvent;

/// <summary>
/// Kolejny fragment „rozumowania" modelu, W TRAKCIE generacji (Zadanie 1/3 planu ROU) — w odróżnieniu
/// od <see cref="ReasoningEvent"/>, który przychodzi RAZ na końcu. Suma delt == treść tego eventu.
/// UI dopisuje delty do akordeonu, żeby użytkownik widział pracę modelu, a nie 40 s ciszy.
/// </summary>
public sealed record ReasoningDeltaEvent(string Text) : ChatEvent;

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

/// <summary>
/// Router uznał, że wiadomość nie wymaga przepisów (Zadanie 8 planu ROU) — baza NIE była
/// przeglądana. UI musi to pokazać JAWNIE i nieusuwalnie: ta odpowiedź nie jest ugruntowana
/// w źródłach, więc nie obowiązuje jej ani bramka abstynencji, ani walidacja cytatów.
/// </summary>
public sealed record NoRetrievalEvent(string Reason) : ChatEvent;

/// <summary>
/// Wartości kolumny <c>messages.Route</c> — którą ścieżką poszła tura. Osobna klasa, nie stałe na
/// <c>ChatService</c>, bo w <c>Chat.razor</c> nazwa <c>ChatService</c> jest zajęta przez wstrzykniętą
/// właściwość (kolizja nazw przy odwołaniu do stałej).
/// </summary>
public static class ChatRoutes
{
    /// <summary>Przeszukano bazę przepisów i orzeczeń.</summary>
    public const string Retrieval = "retrieval";

    /// <summary>Router uznał, że przepisy nie są potrzebne — odpowiedź NIE jest oparta na źródłach.</summary>
    public const string Smalltalk = "smalltalk";
}

/// <summary>Koniec: wynik kontroli anty-fabrykacji (cytaty) + model. <see cref="Usage"/> = tokeny
/// in/out z providera (zbierane zawsze; widoczność w UI steruje flaga Diagnostics:ShowTokenUsage).</summary>
public sealed record DoneEvent(bool Abstained, string? Model, CitationCheck? Check, LlmUsage? Usage = null) : ChatEvent;

/// <summary>Błąd przetwarzania.</summary>
public sealed record ChatErrorEvent(string Message) : ChatEvent;
