namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Checkpoint synchronizacji przyrostowej per źródło. Przesuwany po każdej stronie wyników,
/// by przerwanie ingestii (crash/deploy) nie traciło postępu (zob. plan, „Idempotencja").
/// </summary>
public class SyncStateEntity
{
    /// <summary>Klucz źródła (PK), np. „SAOS".</summary>
    public required string Source { get; set; }

    /// <summary>Najwyższa przetworzona data modyfikacji — punkt startu kolejnego przebiegu.</summary>
    public DateTimeOffset? LastModificationDate { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }
}
