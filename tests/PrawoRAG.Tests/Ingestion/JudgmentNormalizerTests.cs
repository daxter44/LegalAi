using PrawoRAG.Domain.Documents;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>T-NORM — normalizacja orzeczeń SAOS na realnych fixture'ach.</summary>
public class JudgmentNormalizerTests
{
    private readonly JudgmentNormalizer _sut = new();

    [Fact] // T-NORM #1: HTML→tekst, anon-block zachowany, brak tagów i komentarzy
    public void Strips_html_keeps_anon_block_text()
    {
        var doc = _sut.Normalize(SaosFixtures.LoadJudgment(227221));

        Assert.DoesNotContain("<span", doc.PlainText);
        Assert.DoesNotContain("<!--", doc.PlainText);
        Assert.DoesNotContain("<p>", doc.PlainText);
        Assert.Contains("R. I.", doc.PlainText);          // anon-block zachowany jako tekst
        Assert.Contains("nietrzeźwości", doc.PlainText);
    }

    [Fact] // T-NORM #2: zepsuta data nie wywala normalizacji, jest flagowana
    public void Corrupt_date_is_flagged_not_thrown()
    {
        var doc = _sut.Normalize(SaosFixtures.LoadJudgment(31345)); // judgmentDate "3013-12-04"

        Assert.Null(doc.JudgmentDateOrNull());                     // 3013 odrzucone
        Assert.Contains(doc.QualityIssues, i => i.Contains("judgmentDate"));
    }

    [Fact] // T-NORM #3: ekstrakcja metadanych (sygnatura, sąd)
    public void Extracts_core_metadata()
    {
        var doc = _sut.Normalize(SaosFixtures.LoadJudgment(31345));

        Assert.Equal("I ACa 772/13", doc.Locator!.CaseNumber);
        Assert.Equal("Sąd Apelacyjny w Łodzi", doc.Locator!.Court);
        Assert.Contains("Sąd Apelacyjny w Łodzi", doc.Title);
    }

    [Fact] // T-NORM #4: referencedRegulations sparsowane (KK 1997)
    public void Parses_referenced_regulations()
    {
        var doc = _sut.Normalize(SaosFixtures.LoadJudgment(227221));

        var regs = (List<Dictionary<string, object?>>)doc.TypedMetadata["referencedRegulations"]!;
        Assert.NotEmpty(regs);
        Assert.Contains(regs, r => (int?)r["journalYear"] == 1997 &&
                                   (r["journalTitle"] as string ?? "").Contains("Kodeks karny"));
    }

    [Fact] // T-NORM #6: detekcja sekcji (komparycja + sentencja)
    public void Detects_sections()
    {
        var doc = _sut.Normalize(SaosFixtures.LoadJudgment(227221));

        Assert.Contains(doc.Segments, s => s.Label == "komparycja");
        Assert.Contains(doc.Segments, s => s.Label is "sentencja" or "uzasadnienie");
        Assert.All(doc.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    [Fact] // content_hash stabilny i niepusty
    public void Produces_stable_content_hash()
    {
        var a = _sut.Normalize(SaosFixtures.LoadJudgment(227221));
        var b = _sut.Normalize(SaosFixtures.LoadJudgment(227221));
        Assert.Equal(64, a.ContentHash.Length);     // SHA-256 hex
        Assert.Equal(a.ContentHash, b.ContentHash);
    }
}

internal static class NormalizedDocumentTestExtensions
{
    public static DateOnly? JudgmentDateOrNull(this NormalizedDocument d) => d.Locator?.JudgmentDate;
}
