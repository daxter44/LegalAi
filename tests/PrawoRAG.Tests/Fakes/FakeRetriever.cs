using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Fakes;

/// <summary>
/// Deterministyczny <see cref="IRetriever"/>: zwraca wynik wybrany funkcją po tekście zapytania i
/// zapamiętuje WSZYSTKIE otrzymane zapytania. Pozwala testować orkiestrację follow-upu (który tekst
/// trafia do którego toru) bez Postgresa i bez TEI.
/// </summary>
public sealed class FakeRetriever(Func<RetrievalQuery, RetrievalResult> respond) : IRetriever
{
    public List<RetrievalQuery> Queries { get; } = [];

    public Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        return Task.FromResult(respond(query));
    }
}
