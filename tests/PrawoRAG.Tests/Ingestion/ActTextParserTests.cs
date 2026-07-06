using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Segmenter aktu z PŁASKIEGO tekstu (ścieżka PDF) na REALNYM wycinku tekstu jednolitego KK
/// (obwieszczenie DU/2025/383 — po reformie X 2023). Dowodzi: odsianie nagłówków stron i preambuły,
/// segmentacja Art./§, pominięcie jednostek uchylonych, punkty inline oraz AKTUALNOŚĆ treści
/// (art. 37 = „30 lat", nie „15 lat" jak w przestarzałym HTML).
/// </summary>
public class ActTextParserTests
{
    private const string ShortTitle = "Kodeks karny";
    private const string Eli = "DU/2025/383";

    private static (System.Collections.Generic.List<PrawoRAG.Domain.Documents.DocumentSegment> Segments, string Plain) Kk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Eli", "kk_tj_DU_2025_383.slice.txt");
        var text = File.ReadAllText(path);
        return ActTextParser.Parse(text, ShortTitle, Eli, displayAddress: "Dz.U. 2025 poz. 383", sourceUrl: null);
    }

    [Fact] // Nagłówki stron i preambuła obwieszczenia NIE trafiają do segmentów.
    public void Strips_page_headers_and_obwieszczenie_preamble()
    {
        var (segments, _) = Kk();
        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.DoesNotContain("Dziennik Ustaw", s.Text));
        Assert.All(segments, s => Assert.DoesNotContain("OBWIESZCZENIE", s.Text));
        // przepis cytowany w preambule (nowela, „Art. 5. Ustawa wchodzi w życie…") to nie treść KK:
        Assert.DoesNotContain(segments, s => s.Text.Contains("wchodzi w życie po upływie 6 miesięcy"));
    }

    [Fact] // Artykuł z §§ → segment per paragraf; treść we właściwym §.
    public void Splits_article_1_into_paragraphs()
    {
        var art1 = Kk().Segments.Where(s => s.Locator?.Article == "1").ToList();
        Assert.True(art1.Count >= 3, $"Art. 1 ma §§ 1-3; segmentów: {art1.Count}");

        var p1 = art1.Single(s => s.Locator!.Paragraph == "1");
        Assert.Equal("Art. 1 § 1", p1.Label);
        Assert.Equal("article", p1.Kind);
        Assert.Contains("Odpowiedzialności karnej podlega ten tylko", p1.Text);
        Assert.DoesNotContain("szkodliwość jest znikoma", p1.Text); // §2 nie miesza się do §1
    }

    [Fact] // Artykuł bez §§ zostaje jednym segmentem (Paragraph == null).
    public void Article_without_paragraphs_stays_whole()
    {
        var art2 = Kk().Segments.Where(s => s.Locator?.Article == "2").ToList();
        var whole = Assert.Single(art2);
        Assert.Null(whole.Locator!.Paragraph);
        Assert.Contains("przez zaniechanie", whole.Text);
    }

    [Fact] // Punkty wyliczenia zostają w treści segmentu artykułu (v1: bez dzielenia na pkt).
    public void Keeps_enumeration_points_inline()
    {
        var art32 = Kk().Segments.Single(s => s.Locator?.Article == "32");
        Assert.Contains("grzywna", art32.Text);
        Assert.Contains("ograniczenie wolności", art32.Text);
        Assert.Contains("dożywotnie pozbawienie wolności", art32.Text);
    }

    [Fact] // AKTUALNOŚĆ: art. 37 z tekstu jednolitego = „30 lat" (po reformie), a nie „15 lat" (stary HTML).
    public void Reflects_post_2023_reform_via_pdf_text()
    {
        var art37 = Kk().Segments.Single(s => s.Locator?.Article == "37");
        Assert.Contains("30 lat", art37.Text);
        Assert.DoesNotContain("najdłużej 15 lat", art37.Text);
    }

    [Fact] // Jednostki uchylone (art. 36: wszystkie §§ „(uchylony)") nie tworzą pustych segmentów.
    public void Skips_repealed_units()
    {
        var art36 = Kk().Segments.Where(s => s.Locator?.Article == "36").ToList();
        Assert.Empty(art36);
    }

    [Fact] // Nagłówek kontekstowy + lokalizator cytatu.
    public void Context_header_and_locator_are_set()
    {
        var p1 = Kk().Segments.Single(s => s.Locator?.Article == "1" && s.Locator.Paragraph == "1");
        var firstLine = p1.Text.Split('\n')[0];
        Assert.StartsWith("Kodeks karny", firstLine);
        Assert.Contains("Art. 1 § 1", firstLine);
        Assert.Equal(Eli, p1.Locator!.EliId);
        Assert.Equal("Dz.U. 2025 poz. 383", p1.Locator.DisplayAddress);
    }

    [Fact] // Rozporządzenie (brak Art.) — § jako segment najwyższego poziomu.
    public void Parses_top_level_paragraphs_when_no_articles()
    {
        const string reg = "§ 1. Rozporządzenie określa zakres danych. § 2. Dane przekazuje się elektronicznie.";
        var (segments, _) = ActTextParser.Parse(reg, "Rozporządzenie testowe", "DU/2023/2824", null, null);
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Null(s.Locator?.Article));
        Assert.Contains(segments, s => s.Locator?.Paragraph == "1");
        Assert.Contains(segments, s => s.Label is not null && s.Label.StartsWith('§'));
    }
}
