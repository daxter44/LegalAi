using PrawoRAG.Api.Services;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-SEARCH — grupowanie wyników wyszukiwarki per dokument (czysta funkcja, bez DB): jeden dokument =
/// jedna karta reprezentowana przez najlepiej scorowany fragment, kolejność po najlepszym score,
/// licznik fragmentów, lokalizator i podstawy prawne przeniesione.
/// </summary>
public class SearchGroupingTests
{
    private static RetrievedChunk Chunk(Guid doc, string text, double score,
        string title = "Tytuł", string? caseNumber = null, IReadOnlyList<string>? legalBases = null) => new()
    {
        ChunkId = Guid.NewGuid(), DocumentId = doc, Text = text, Source = SourceKeys.Nsa,
        DocType = DocTypes.Judgment, Title = title, Score = score,
        Locator = caseNumber is null ? null : new CitationLocator { CaseNumber = caseNumber, Court = "WSA w Opolu" },
        LegalBases = legalBases,
    };

    [Fact]
    public void Groups_by_document_best_fragment_is_snippet_and_count_correct()
    {
        var docA = Guid.NewGuid();
        var docB = Guid.NewGuid();
        var hits = SearchGrouping.Group(
        [
            Chunk(docA, "Fragment A słabszy", 0.2),
            Chunk(docA, "Fragment A najlepszy", 0.9, caseNumber: "II SA/Op 8/05"),
            Chunk(docB, "Fragment B", 0.5),
        ]);

        Assert.Equal(2, hits.Count);
        // Dokument A ma wyższy najlepszy score → pierwszy.
        Assert.Equal(docA, hits[0].DocumentId);
        Assert.Equal("Fragment A najlepszy", hits[0].Snippet);   // reprezentuje najlepszy fragment
        Assert.Equal(2, hits[0].FragmentCount);                  // oba fragmenty A policzone
        Assert.Contains("II SA/Op 8/05", hits[0].Locator);       // lokalizator z najlepszego fragmentu
        Assert.Equal(docB, hits[1].DocumentId);
        Assert.Equal(1, hits[1].FragmentCount);
    }

    [Fact]
    public void Legal_bases_carried_from_best_fragment()
    {
        var doc = Guid.NewGuid();
        var hits = SearchGrouping.Group(
            [Chunk(doc, "treść", 0.8, legalBases: ["art. 145 § 1 pkt 1 ppsa"])]);

        Assert.Single(hits);
        Assert.Equal(["art. 145 § 1 pkt 1 ppsa"], hits[0].LegalBases);
    }

    [Fact]
    public void Long_text_is_trimmed_and_whitespace_collapsed()
    {
        var doc = Guid.NewGuid();
        var hits = SearchGrouping.Group([Chunk(doc, "wiele   \n  spacji\ttu", 0.5)]);
        Assert.Equal("wiele spacji tu", hits[0].Snippet);
    }

    [Fact]
    public void Empty_input_yields_empty()
    {
        Assert.Empty(SearchGrouping.Group([]));
    }
}
