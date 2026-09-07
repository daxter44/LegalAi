using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Api.Services;

/// <summary>Jeden dokument w wynikach wyszukiwarki (retrieval-only): zgrupowane fragmenty jednego
/// orzeczenia/aktu. <see cref="Snippet"/> = najlepiej dopasowany fragment; <see cref="FragmentCount"/>
/// = ile fragmentów tego dokumentu trafiło w zwróconą pulę.</summary>
public sealed record SearchHit(
    Guid DocumentId,
    string Title,
    string Locator,
    string? Url,
    string Snippet,
    int FragmentCount,
    double Score,
    IReadOnlyList<string>? LegalBases);

/// <summary>
/// Grupowanie surowych chunków z retrievera w wyniki PER DOKUMENT dla strony „Wyszukiwarka".
/// Czysta funkcja (testowalna bez DB): jeden dokument = jedna karta, reprezentowana przez
/// najlepiej scorowany fragment; dokumenty w kolejności najlepszego fragmentu. Bez LLM, bez
/// bramki abstynencji — wyszukiwarka zawsze pokazuje ranking tego, co jest w korpusie.
/// </summary>
public static class SearchGrouping
{
    public static IReadOnlyList<SearchHit> Group(IReadOnlyList<RetrievedChunk> chunks)
    {
        return chunks
            .GroupBy(c => c.DocumentId)
            .Select(g =>
            {
                var best = g.OrderByDescending(c => c.Score).First();
                return new SearchHit(
                    DocumentId: g.Key,
                    Title: best.Title,
                    Locator: GroundedPrompt.LocatorLabel(best),
                    Url: best.SourceUrl,
                    Snippet: Snippet(best.Text),
                    FragmentCount: g.Count(),
                    Score: best.Score,
                    LegalBases: best.LegalBases);
            })
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    private static string Snippet(string text, int max = 320)
    {
        var clean = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= max ? clean : clean[..max] + "…";
    }
}
