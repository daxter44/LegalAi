using System.Text.Json;

namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Pojedyncza wiadomość w rozmowie. Dla odpowiedzi asystenta zapisujemy KONTEKST decyzji
/// (zwrócone źródła, abstynencja, czystość cytatów) — to materiał do golden setu i kalibracji.
/// </summary>
public class MessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public ConversationEntity? Conversation { get; set; }

    /// <summary>„user" albo „assistant".</summary>
    public required string Role { get; set; }
    public required string Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // --- kontekst decyzji (tylko dla odpowiedzi asystenta) ---
    /// <summary>Zwrócone źródła (jsonb: lista {index, label, url}) — co retrieval podał do generacji.</summary>
    public JsonDocument? RetrievedSources { get; set; }

    /// <summary>Czy system odmówił (brak pokrycia).</summary>
    public bool Abstained { get; set; }

    /// <summary>Wynik anty-fabrykacji: true=czyste cytaty, false=podejrzane, null=nie dotyczy (odmowa).</summary>
    public bool? CitationClean { get; set; }

    public string? Model { get; set; }

    public FeedbackEntity? Feedback { get; set; }
}
