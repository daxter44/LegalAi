using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Analysis;

/// <summary>AJ-6 — nagłówek raportu liczony mechanicznie z werdyktów: liczby, lista fragmentów
/// z ryzykiem wg wagi, kategorie poboczne tylko gdy niezerowe, pusty raport → pusty nagłówek.</summary>
public class AnalysisReportTests
{
    private static UnitDigest U(string h, UnitVerdict v) => new(h, v, "x");

    [Fact]
    public void Mixed_verdicts_are_counted_and_risks_listed_by_weight()
    {
        var h = AnalysisReport.Headline(
        [
            U("wstęp", UnitVerdict.NoLegalContent), U("§ 1", UnitVerdict.Ok), U("§ 2", UnitVerdict.RiskHigh),
            U("§ 3", UnitVerdict.RiskLow), U("§ 4", UnitVerdict.Risk), U("§ 5", UnitVerdict.OutOfScope),
            U("§ 6", UnitVerdict.NoSources), U("§ 7", UnitVerdict.Error),
        ]);
        Assert.Equal(
            "3 z 8 fragmentów z ryzykiem (wysokie: § 2, § 4; niskie: § 3); 1 poza zakresem korpusu; " +
            "1 bez podstawy w źródłach; 1 bez treści prawnej; 1 z błędem lub bez wyniku.", h);
    }

    [Fact]
    public void All_ok_says_no_risk_and_omits_zero_categories()
    {
        var h = AnalysisReport.Headline([U("§ 1", UnitVerdict.Ok), U("§ 2", UnitVerdict.Ok)]);
        Assert.Equal("Brak fragmentów z ryzykiem wśród 2 fragmentów.", h);
    }

    [Fact]
    public void Only_low_risks_lists_them_under_low()
    {
        var h = AnalysisReport.Headline([U("§ 1", UnitVerdict.RiskLow), U("§ 2", UnitVerdict.Ok), U("§ 3", UnitVerdict.Ok)]);
        Assert.StartsWith("1 z 3 fragmentów z ryzykiem (niskie: § 1)", h);
        Assert.DoesNotContain("wysokie", h);
    }

    [Fact]
    public void Single_unit_uses_singular_and_empty_input_gives_empty_headline()
    {
        Assert.Equal("Brak fragmentów z ryzykiem wśród 1 fragmentu.", AnalysisReport.Headline([U("§ 1", UnitVerdict.Ok)]));
        Assert.Equal("", AnalysisReport.Headline([]));
    }
}
