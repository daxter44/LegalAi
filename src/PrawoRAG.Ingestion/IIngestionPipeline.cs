using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion;

/// <summary>
/// Kontrakt rdzenia ingestii — wydzielony z <see cref="IngestionPipeline"/>, by runnery dały się
/// testować jednostkowo (fake sterujący sekwencją wyników: serie Failed dla bezpiecznika,
/// weryfikacja że fast-skip w ogóle nie woła pipeline'u).
/// </summary>
public interface IIngestionPipeline
{
    Task<IngestOutcome> ProcessAsync(RawDocument raw, CancellationToken ct);
}
