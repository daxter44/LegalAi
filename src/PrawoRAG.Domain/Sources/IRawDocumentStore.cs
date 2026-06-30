namespace PrawoRAG.Domain.Sources;

/// <summary>
/// Trwały, przenośny magazyn SUROWYCH dokumentów (warstwa „raw", przed normalizacją).
/// Odcina re-processing od źródła: po jednorazowym <c>fetch</c> można w kółko zmieniać
/// normalizer/chunker/model i przetwarzać korpus OFFLINE, bez ponownego pobierania z API.
/// Klucz naturalny dokumentu = (<paramref name="source"/>, <c>externalId</c>).
/// </summary>
public interface IRawDocumentStore
{
    /// <summary>Czy surowy dokument jest już w magazynie (idempotencja fetchu — nie pobieraj ponownie).</summary>
    Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct);

    /// <summary>Zapisuje surowy dokument atomowo (zapis tymczasowy + rename → crash nie zostawia połówek).</summary>
    Task SaveAsync(RawDocument document, CancellationToken ct);

    /// <summary>Strumieniuje leniwie wszystkie surowe dokumenty danego źródła. Działa offline.</summary>
    IAsyncEnumerable<RawDocument> EnumerateAsync(string source, CancellationToken ct);

    /// <summary>Liczba surowych dokumentów danego źródła w magazynie.</summary>
    Task<int> CountAsync(string source, CancellationToken ct);
}
