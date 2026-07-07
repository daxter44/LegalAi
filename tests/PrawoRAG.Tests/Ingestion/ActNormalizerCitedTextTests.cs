using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Regresja realnego buga (wykryty na M4): obwieszczenie z tekstem jednolitym w HTML ma sekcję „Treść
/// obwieszczenia" (rejestr zmian) cytującą przepisy ustaw NOWELIZUJĄCYCH — oznaczone klasą „pro-cite-text".
/// Mają te same numery co prawdziwe artykuły z załącznika (np. „Art. 93" = klauzula wejścia w życie obcej
/// ustawy vs realny art. 93 KSH o spółce partnerskiej). Bez wykluczenia cytowanych obie treści trafiały do
/// bazy pod tym samym numerem. Test dowodzi, że bierzemy TYLKO „pro-text" (załącznik).
/// </summary>
public class ActNormalizerCitedTextTests
{
    private static PrawoRAG.Domain.Documents.NormalizedDocument Normalize()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Eli");
        var html = File.ReadAllText(Path.Combine(dir, "obwieszczenie_kolizja.html"));
        using var meta = JsonDocument.Parse(
            """{"title":"Ustawa z dnia 15 września 2000 r. - Kodeks spółek handlowych","displayAddress":"Dz.U. 2024 poz. 18"}""");
        var raw = new RawDocument
        {
            Source = SourceKeys.Eli,
            ExternalId = "DU/2000/1037",
            DocType = DocTypes.Act,
            RawContent = html,
            SourcePayload = meta.RootElement.Clone(),
        };
        return new ActNormalizer().Normalize(raw);
    }

    [Fact]
    public void Skips_cited_preamble_articles_keeps_real_annex_text()
    {
        var doc = Normalize();

        var art93 = doc.Segments.Where(s => s.Locator?.Article == "93").ToList();
        var seg = Assert.Single(art93);                       // dokładnie jeden art. 93 — nie dublet cyt./realny
        Assert.Contains("spółki partnerskiej", seg.Text);     // prawdziwy przepis z załącznika (pro-text)
        Assert.DoesNotContain("wchodzi w życie", seg.Text);   // cytowana klauzula (pro-cite-text) odrzucona

        // Cytowana klauzula NIE pojawia się w żadnym segmencie dokumentu.
        Assert.DoesNotContain(doc.Segments, s => s.Text.Contains("wchodzi w życie po upływie 30 dni"));
        // Prawdziwe artykuły z załącznika są obecne.
        Assert.Contains(doc.Segments, s => s.Locator?.Article == "94");
    }
}
