using PrawoRAG.Ingestion.Eli;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-ACT — normalizacja aktów ELI na REALNYM fixture KK (DU/1997/553). Dowodzi deterministycznego
/// parsowania artykułów I PARAGRAFÓW (artykuł z ≥2 §§ → segment na §; jeden § = jedna norma
/// = jeden wektor), nagłówka kontekstowego i lokalizatora cytatu. Bez sieci i bazy.
/// </summary>
public class ActNormalizerTests
{
    private readonly ActNormalizer _sut = new();
    private PrawoRAG.Domain.Documents.NormalizedDocument Kk() => _sut.Normalize(EliFixtures.LoadAct("DU/1997/553"));

    [Fact] // A1: artykuł 148 (4 §§) rozbity na segment per paragraf
    public void Splits_article_148_into_paragraph_segments()
    {
        var art148 = Kk().Segments.Where(s => s.Locator?.Article == "148").ToList();
        Assert.True(art148.Count >= 4, $"Art. 148 ma §§ 1-4; segmentów: {art148.Count}");

        var p1 = art148.Single(s => s.Locator!.Paragraph == "1");
        Assert.Equal("Art. 148 § 1", p1.Label);
        Assert.Equal("article", p1.Kind);
        Assert.Contains("Kto zabija człowieka", p1.Text);
        // §2 z §4 nie mieszają się do wektora §1:
        Assert.DoesNotContain("ze szczególnym okrucieństwem", p1.Text);
        Assert.DoesNotContain("silnego wzburzenia", p1.Text);
    }

    [Fact] // A1b: punkty 1)–4) zostają WEWNĄTRZ swojego paragrafu (nie rozbijamy niżej niż §)
    public void Points_stay_inside_their_paragraph()
    {
        var p2 = Kk().Segments.Single(s => s.Locator?.Article == "148" && s.Locator.Paragraph == "2");
        Assert.Contains("ze szczególnym okrucieństwem", p2.Text);
        Assert.Contains("broni palnej", p2.Text);
    }

    [Fact] // A1c: artykuł bez §§ zostaje jednym segmentem (Paragraph == null)
    public void Article_without_paragraphs_stays_whole()
    {
        var whole = Kk().Segments.Where(s => s.Locator?.Paragraph is null && s.Locator?.Article is not null).ToList();
        Assert.NotEmpty(whole); // KK ma ~126 artykułów bez unit_para
        Assert.All(whole, s => Assert.DoesNotContain("§", s.Label!));
    }

    [Fact] // A2: nagłówek kontekstowy — krótka nazwa aktu (mniej bojlerplate'u w wektorze), art + §
    public void Segment_text_starts_with_context_header()
    {
        var p1 = Kk().Segments.Single(s => s.Locator?.Article == "148" && s.Locator.Paragraph == "1");
        var firstLine = p1.Text.Split('\n')[0];
        Assert.StartsWith("Kodeks karny", firstLine);   // krótka nazwa, nie „Ustawa z dnia…"
        Assert.Contains("Art. 148 § 1", firstLine);
    }

    [Fact] // A3: lokalizator cytatu — eli_id, kotwica per-§, displayAddress, paragraf
    public void Locator_has_eli_anchor_and_display_address()
    {
        var p1 = Kk().Segments.Single(s => s.Locator?.Article == "148" && s.Locator.Paragraph == "1");
        Assert.Equal("DU/1997/553", p1.Locator!.EliId);
        Assert.Equal("Dz.U. 1997 nr 88 poz. 553", p1.Locator.DisplayAddress);
        Assert.Contains("arti_148", p1.Locator.Anchor);
        Assert.Contains("para_1", p1.Locator.Anchor);   // kotwica wskazuje konkretny §
    }

    [Fact] // A4: obowiązywanie z metadanych (IN_FORCE → true)
    public void Marks_in_force_from_metadata()
    {
        Assert.Equal(true, Kk().TypedMetadata["inForce"]);
    }

    [Fact] // A5: stabilny hash + segmentacja całego KK (deterministycznie, więcej segmentów niż artykułów)
    public void Produces_stable_hash_and_many_segments()
    {
        var a = Kk();
        var b = Kk();
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(64, a.ContentHash.Length);
        // KK: ~363 artykuły, z czego ~237 z §§ (691 unit_para) → po podziale znacznie ponad 600 segmentów.
        Assert.True(a.Segments.Count > 600, $"KK po podziale per § ma setki segmentów; znaleziono {a.Segments.Count}");
        Assert.StartsWith("Ustawa z dnia 6 czerwca 1997 r. - Kodeks karny", a.Title); // pełny tytuł zostaje w Title
    }
}
