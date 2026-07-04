using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-ACT — normalizacja aktów ELI na REALNYM fixture KK (DU/1997/553). Dowodzi deterministycznego
/// parsowania artykułów, nagłówka kontekstowego i lokalizatora cytatu. Bez sieci i bazy.
/// </summary>
public class ActNormalizerTests
{
    private readonly ActNormalizer _sut = new();
    private PrawoRAG.Domain.Documents.NormalizedDocument Kk() => _sut.Normalize(EliFixtures.LoadAct("DU/1997/553"));

    [Fact] // A1: artykuł 148 z paragrafami
    public void Parses_article_148_with_paragraphs()
    {
        var art = Kk().Segments.FirstOrDefault(s => s.Locator?.Article == "148");
        Assert.NotNull(art);
        Assert.Equal("Art. 148", art!.Label);
        Assert.Equal("article", art.Kind);
        Assert.Contains("Kto zabija człowieka", art.Text);
        Assert.Contains("§ 1", art.Text);
    }

    [Fact] // A2: nagłówek kontekstowy wbity na początek segmentu (chunk samoopisowy)
    public void Segment_text_starts_with_context_header()
    {
        var art = Kk().Segments.First(s => s.Locator?.Article == "148");
        var firstLine = art.Text.Split('\n')[0];
        Assert.StartsWith("Ustawa", firstLine);       // tytuł aktu
        Assert.Contains("Art. 148", firstLine);       // numer artykułu w nagłówku
    }

    [Fact] // A3: lokalizator cytatu — eli_id, kotwica HTML, displayAddress
    public void Locator_has_eli_anchor_and_display_address()
    {
        var art = Kk().Segments.First(s => s.Locator?.Article == "148");
        Assert.Equal("DU/1997/553", art.Locator!.EliId);
        Assert.Equal("Dz.U. 1997 nr 88 poz. 553", art.Locator.DisplayAddress);
        Assert.Contains("arti_148", art.Locator.Anchor);
    }

    [Fact] // A4: obowiązywanie z metadanych (IN_FORCE → true)
    public void Marks_in_force_from_metadata()
    {
        Assert.Equal(true, Kk().TypedMetadata["inForce"]);
    }

    [Fact] // A5: stabilny hash + setki artykułów (deterministyczne parsowanie całego KK)
    public void Produces_stable_hash_and_many_articles()
    {
        var a = Kk();
        var b = Kk();
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(64, a.ContentHash.Length);
        Assert.True(a.Segments.Count > 300, $"KK ma setki artykułów; znaleziono {a.Segments.Count}");
        Assert.StartsWith("Ustawa z dnia 6 czerwca 1997 r. - Kodeks karny", a.Title);
    }
}
