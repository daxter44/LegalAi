using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Regresja realnego buga (wykryty na M4, „ustrój sądów powszechnych"): tekst jednolity zawiera artykuł
/// w DWÓCH brzmieniach (obowiązującym i wchodzącym w życie z przyszłą datą) — OBA jako `pro-text`. Bez
/// rozróżnienia dwa chunki lądują pod tym samym „Art. 175da". Nie usuwamy (który obowiązuje zależy od
/// daty i bywa nietrwałe): oznaczamy wariantami + zgłaszamy QualityIssue, żeby nie było cichej kolizji.
/// </summary>
public class ActNormalizerDuplicateUnitTests
{
    private static PrawoRAG.Domain.Documents.NormalizedDocument Normalize()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Eli");
        var html = File.ReadAllText(Path.Combine(dir, "obwieszczenie_dwie_wersje.html"));
        using var meta = JsonDocument.Parse(
            """{"title":"Ustawa z dnia 27 lipca 2001 r. - Prawo o ustroju sądów powszechnych"}""");
        var raw = new RawDocument
        {
            Source = SourceKeys.Eli, ExternalId = "DU/2001/1070", DocType = DocTypes.Act,
            RawContent = html, SourcePayload = meta.RootElement.Clone(),
        };
        return new ActNormalizer().Normalize(raw);
    }

    [Fact]
    public void Duplicate_article_versions_are_disambiguated_and_flagged()
    {
        var doc = Normalize();

        var v = doc.Segments.Where(s => s.Locator?.Article == "175da").ToList();
        Assert.Equal(2, v.Count);                                             // obie wersje zachowane
        Assert.Contains(v, s => s.Label!.Contains("wariant 1/2"));            // rozróżnione etykietą
        Assert.Contains(v, s => s.Label!.Contains("wariant 2/2"));
        Assert.All(v, s => Assert.Contains("wariant", s.Text.Split('\n')[0]));// wariant też w treści (cytowanie)

        // Obie treści zachowane (bezstratnie), w tym wersja z przyszłą datą wejścia w życie:
        Assert.Contains(v, s => s.Text.Contains("bieżące czynności"));
        Assert.Contains(v, s => s.Text.Contains("14 marca 2024"));

        // Zgłoszone do przeglądu:
        Assert.Contains(doc.QualityIssues, i => i.Contains("Duplikat") && i.Contains("175da"));

        // Zwykły artykuł bez duplikatu — bez sufiksu wariantu:
        var art176 = Assert.Single(doc.Segments.Where(s => s.Locator?.Article == "176"));
        Assert.DoesNotContain("wariant", art176.Label!);
    }
}
