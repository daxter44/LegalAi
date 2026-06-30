using System.Runtime.CompilerServices;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Atrapa <see cref="ISourceConnector"/> — strumieniuje ustalony zestaw dokumentów i ZLICZA
/// wywołania <see cref="FetchAsync"/>. Pozwala dowieść, że faza „process" nie sięga do źródła.
/// </summary>
public sealed class FakeSourceConnector(string source, IReadOnlyList<RawDocument> docs) : ISourceConnector
{
    public int FetchCalls { get; private set; }

    public string Source => source;

    public async IAsyncEnumerable<RawDocument> FetchAsync(FetchRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        FetchCalls++;
        foreach (var d in docs)
        {
            ct.ThrowIfCancellationRequested();
            yield return d;
        }
        await Task.CompletedTask;
    }
}
