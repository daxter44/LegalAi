using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Deterministyczny <see cref="IReranker"/> do testów (bez TEI): pasażowi zawierającemu
/// <paramref name="boostSubstring"/> daje wysoki score, pozostałym niski — pozwala sprawdzić, że
/// retriever przestawia kolejność wg rerankera i że jego top-score steruje sygnałem abstynencji.
/// </summary>
public sealed class FakeReranker(string boostSubstring, double high = 0.99, double low = 0.10) : IReranker
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<string> passages, CancellationToken ct)
    {
        Calls++;
        IReadOnlyList<RerankResult> res = passages
            .Select((p, i) => new RerankResult(i, p.Contains(boostSubstring, StringComparison.OrdinalIgnoreCase) ? high : low))
            .ToList();
        return Task.FromResult(res);
    }
}
