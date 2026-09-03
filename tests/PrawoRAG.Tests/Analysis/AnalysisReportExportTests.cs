using PrawoRAG.Api.Services;
using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Analysis;

/// <summary>AJ-12 — eksport raportu do Markdownu: nagłówek, streszczenie, per fragment werdykt +
/// narusza/do rozważenia + uzasadnienie + źródła z linkami; tryb z archiwum (bez treści §) daje
/// raport bez cytatów, ale z werdyktami; jednostki bez wyniku oznaczone.</summary>
public class AnalysisReportExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(2));

    private static AnalysisSnapshot Snap(bool withText, AnalysisStatus status = AnalysisStatus.Done)
    {
        var units = new List<DocUnit>
        {
            new(1, "§ 1", withText ? "§ 1 Przedmiot najmu: lokal nr 4." : ""),
            new(2, "§ 5", withText ? "§ 5 Kaucja 80 000 zł, nie podlega oprocentowaniu." : ""),
            new(3, "§ 6", withText ? "§ 6 Wydanie lokalu protokołem." : ""),
        };
        var results = new UnitAnalysis?[]
        {
            new(1, "§ 1", UnitVerdict.NoLegalContent, "Opis przedmiotu, brak postanowienia do oceny.", []),
            new(2, "§ 5", UnitVerdict.RiskHigh, "Kaucja przekracza ustawowy limit [1].",
                [new ChatSource(1, "art. 6 uopl", "Ustawa o ochronie praw lokatorów", "https://isap.sejm.gov.pl/x", "…")],
                Violates: "art. 6 ust. 1 uopl [1]", Suggestion: "obniżyć kaucję do 12-krotności czynszu"),
            status == AnalysisStatus.Interrupted ? null : new(3, "§ 6", UnitVerdict.Ok, "Zgodne [1].",
                [new ChatSource(1, "art. 675 KC", "Kodeks cywilny", null, "…")]),
        };
        return new AnalysisSnapshot(Guid.NewGuid(), "umowa.pdf", 2, "oceń ryzyka najemcy", status, units, false,
            results, results.Count(r => r is not null), status == AnalysisStatus.Done ? "Jedno ryzyko wysokie w § 5." : null, null);
    }

    [Fact]
    public void Full_report_has_all_sections_in_order()
    {
        var md = AnalysisReportExport.ToMarkdown(Snap(withText: true), Now);

        Assert.StartsWith("# Analiza dokumentu: umowa.pdf", md);
        Assert.Contains("Polecenie: oceń ryzyka najemcy", md);
        Assert.Contains("Data: 2026-09-03 12:00", md);
        Assert.Contains("**1 z 3 fragmentów z ryzykiem (wysokie: § 5); 1 bez treści prawnej.**", md);
        Assert.Contains("## Streszczenie\n\nJedno ryzyko wysokie w § 5.", md);
        Assert.Contains("### § 5 — RYZYKO WYSOKIE", md);
        Assert.Contains("> § 5 Kaucja 80 000 zł", md);
        Assert.Contains("- **Narusza:** art. 6 ust. 1 uopl [1]", md);
        Assert.Contains("- **Do rozważenia:** obniżyć kaucję", md);
        Assert.Contains("Kaucja przekracza ustawowy limit [1].", md);
        Assert.Contains("- [1] art. 6 uopl — Ustawa o ochronie praw lokatorów <https://isap.sejm.gov.pl/x>", md);
        Assert.Contains("- [1] art. 675 KC — Kodeks cywilny\n", md); // bez URL → bez nawiasów
        Assert.Contains("### § 1 — BEZ TREŚCI PRAWNEJ", md);
        Assert.True(md.IndexOf("## Streszczenie") < md.IndexOf("## Fragmenty"));
        Assert.True(md.IndexOf("### § 1") < md.IndexOf("### § 5"));
        Assert.EndsWith("nie jest przechowywana po stronie usługi.\n", md);
    }

    [Fact]
    public void Archive_snapshot_without_text_has_no_quotes_but_keeps_verdicts()
    {
        var md = AnalysisReportExport.ToMarkdown(Snap(withText: false), Now);
        Assert.DoesNotContain("> ", md);
        Assert.Contains("### § 5 — RYZYKO WYSOKIE", md);
        Assert.Contains("- **Narusza:**", md);
    }

    [Fact]
    public void Interrupted_report_marks_partial_and_unanalysed_units()
    {
        var md = AnalysisReportExport.ToMarkdown(Snap(withText: true, AnalysisStatus.Interrupted), Now);
        Assert.Contains("Fragmentów: 2 z 3 (analiza przerwana — raport częściowy)", md);
        Assert.Contains("### § 6 — nieprzeanalizowany", md);
        Assert.DoesNotContain("## Streszczenie", md);
    }

    [Fact]
    public void Long_unit_text_is_trimmed_to_snippet()
    {
        var units = new List<DocUnit> { new(1, "§ 1", "§ 1 " + new string('x', 1000)) };
        var snap = new AnalysisSnapshot(Guid.NewGuid(), "u.pdf", 1, "p", AnalysisStatus.Done, units, false,
            [new UnitAnalysis(1, "§ 1", UnitVerdict.Ok, "ok", [])], 1, null, null);
        var md = AnalysisReportExport.ToMarkdown(snap, Now);
        var quote = md.Split('\n').Single(l => l.StartsWith("> "));
        Assert.True(quote.Length <= AnalysisReportExport.SnippetChars + 4);
        Assert.EndsWith("…", quote);
    }
}
