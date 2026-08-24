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

    /// <summary>
    /// Którą ścieżką poszła tura (Zadanie 8 planu ROU): <c>retrieval</c> = przeszukano bazę,
    /// <c>smalltalk</c> = router uznał, że przepisy nie są potrzebne (odpowiedź NIE jest oparta
    /// na źródłach). Null = wiadomość zapisana przed wprowadzeniem routera.
    /// Bez tego pola raport z żywego ruchu nie odróżni ścieżek, a trafność routera byłaby
    /// niemierzalna — czyli włączenie go na produkcji byłoby wiarą, nie decyzją.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>
    /// Czy odpowiedź była REGENEROWANA przez bramkę anty-fabrykacji (Zadanie 10 planu ROU) — pierwsza
    /// wersja powołała się na artykuł/sygnaturę nieobecne w kontekście. Materiał do pomiaru
    /// fałszywych alarmów bramki (próg zabicia: >10% regeneracji na poprawnych odpowiedziach).
    /// </summary>
    public bool Regenerated { get; set; }

    public string? Model { get; set; }

    public FeedbackEntity? Feedback { get; set; }
}
