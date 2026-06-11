namespace PrawoRAG.Domain.Sources;

/// <summary>
/// Konektor źródła danych. Jedna implementacja na źródło (SAOS, ELI, …).
/// Strumieniuje dokumenty zmienione od <see cref="FetchRequest.SinceModificationDate"/>
/// w kolejności rosnącej daty modyfikacji, by pipeline mógł checkpointować postęp po każdej porcji.
/// Dodanie nowego źródła = nowa implementacja + rejestracja w DI (po <see cref="Source"/>).
/// </summary>
public interface ISourceConnector
{
    /// <summary>Klucz źródła (np. <see cref="SourceKeys.Saos"/>).</summary>
    string Source { get; }

    /// <summary>
    /// Pobiera dokumenty leniwie (stronicowanie wewnątrz implementacji). Honoruje anulowanie,
    /// retry i rate-limit po stronie implementacji. Nie musi deduplikować — to robi pipeline po
    /// kluczu naturalnym i <c>content_hash</c>.
    /// </summary>
    IAsyncEnumerable<RawDocument> FetchAsync(FetchRequest request, CancellationToken ct);
}
