using PrawoRAG.Eval;
using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// AJ-0 — golden set analizy dokumentów (<c>analysis-set.json</c>) musi być spójny ze splitterem:
/// klucz odpowiedzi jest układany PER NAGŁÓWEK, więc każda zmiana <see cref="LegalUnitSplitter"/>
/// (albo literówka w tekście dokumentu), która przesuwa/gubi jednostkę, unieważniłaby klucz po cichu.
/// Ten test zamienia to na czerwony build.
/// </summary>
public class AnalysisGoldenSetTests
{
    private static async Task<IReadOnlyList<AnalysisGoldenDoc>> LoadAsync() =>
        await AnalysisGoldenDoc.LoadAsync(AnalysisGoldenDoc.DefaultPath());

    [Fact]
    public async Task Set_loads_and_ids_are_unique()
    {
        var docs = await LoadAsync();
        Assert.True(docs.Count >= 4, $"golden set analizy ma {docs.Count} dokumentów, plan AJ-0 wymaga 4–6");
        Assert.Equal(docs.Count, docs.Select(d => d.Id).Distinct().Count());
        Assert.All(docs, d => Assert.False(string.IsNullOrWhiteSpace(d.Prompt)));
    }

    [Fact]
    public async Task Every_document_splits_into_exactly_the_keyed_headings()
    {
        var docs = await LoadAsync();
        foreach (var doc in docs)
        {
            var units = LegalUnitSplitter.Split(doc.Pages);
            var actual = units.Select(u => u.Heading).ToList();
            var expected = doc.Units.Select(u => u.Heading).ToList();
            Assert.True(expected.SequenceEqual(actual),
                $"[{doc.Id}] nagłówki ze splittera ≠ klucz.\n  splitter: {string.Join(" | ", actual)}\n  klucz:    {string.Join(" | ", expected)}");
        }
    }

    [Fact]
    public async Task Planted_risks_are_marked_risk_and_have_description()
    {
        var docs = await LoadAsync();
        foreach (var u in docs.SelectMany(d => d.Units))
        {
            if (u.PlantedRisk is not null)
                Assert.True(u.ExpectedVerdict == ExpectedVerdict.Risk || u.NeedsLawyer,
                    $"{u.Heading}: opis wady bez werdyktu Risk");
            if (u.ExpectedArticle is not null)
                Assert.NotNull(u.ExpectedEli); // artykuł bez aktu jest niescorowalny
        }
    }

    [Fact]
    public async Task Negative_control_document_has_no_expected_risk()
    {
        var docs = await LoadAsync();
        var control = Assert.Single(docs, d => d.Id == "dzielo-b2b");
        Assert.DoesNotContain(control.Units, u => u.ExpectedVerdict == ExpectedVerdict.Risk);
    }
}
