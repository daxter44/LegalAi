namespace PrawoRAG.Domain.Sources;

/// <summary>
/// Parametry pobierania dla konektora. Filtry specyficzne dla źródła (np. wycinek korpusu MVP:
/// typ sądu, dziedzina) konektor czyta z własnej konfiguracji w DI — tu trzymamy tylko to,
/// co wspólne i sterujące synchronizacją przyrostową.
/// </summary>
public sealed record FetchRequest
{
    /// <summary>Pobieraj tylko dokumenty zmienione po tej dacie (checkpoint z <c>sync_state</c>). Null = od początku.</summary>
    public DateTimeOffset? SinceModificationDate { get; init; }

    /// <summary>Twardy limit liczby dokumentów (testy/spike'i). Null = bez limitu.</summary>
    public int? MaxItems { get; init; }
}
