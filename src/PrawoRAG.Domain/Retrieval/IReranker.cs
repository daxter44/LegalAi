namespace PrawoRAG.Domain.Retrieval;

/// <summary>Wynik rerankingu: indeks pasażu w wejściowej liście + score trafności (wyższy = trafniejszy).</summary>
public sealed record RerankResult(int Index, double Score);

/// <summary>
/// Reranker (cross-encoder): przelicza trafność każdego pasażu względem zapytania i daje score
/// dużo lepiej rozdzielony niż surowy cosine. Wymienny (TEI /rerank; później inny) — jak
/// <see cref="Embeddings.IEmbeddingProvider"/>. Gdy podłączony, jego top-score steruje bramką abstynencji.
/// </summary>
public interface IReranker
{
    /// <summary>Zwraca score dla każdego pasażu (kolejność wyniku nieistotna — liczy się <see cref="RerankResult.Index"/>).</summary>
    Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct);
}
