using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// <see cref="IReranker"/>, który zawsze rzuca — odwzorowuje realną awarię TEI w locie: ubity spot VM,
/// restart kontenera, timeout, 503. <see cref="TeiReranker"/> rzuca w takich razach
/// <see cref="HttpRequestException"/>, więc fake używa tego samego typu.
/// </summary>
public sealed class ThrowingReranker : IReranker
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        Calls++;
        throw new HttpRequestException("TEI /rerank 503 Service Unavailable: symulacja awarii");
    }
}
