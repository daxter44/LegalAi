using PrawoRAG.Ingestion.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Ekstraktor PDF (wrapper na PdfPig). Dowodzi: poprawny PDF born-digital → tekst wszystkich stron;
/// puste bajty → jasny wyjątek (wołający kwarantannuje). Realną jakość ekstrakcji polskich tekstów
/// jednolitych dowodzą testy segmentera na fixture tekstowym — tu izolujemy sam wrapper.
/// </summary>
public class PdfTextExtractorTests
{
    private static byte[] BuildPdf(params string[] pagesText)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var line in pagesText)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(line, 12, new PdfPoint(50, 700), font);
        }
        return builder.Build();
    }

    [Fact]
    public void Extracts_text_from_all_pages()
    {
        var pdf = BuildPdf("Art. 37 max 30 lat", "Art. 38 obostrzenie");
        var text = new PdfPigTextExtractor().ExtractText(pdf);

        Assert.Contains("Art. 37", text);
        Assert.Contains("30 lat", text);
        Assert.Contains("Art. 38", text); // druga strona też
    }

    [Fact]
    public void Empty_bytes_throw_argument_exception()
        => Assert.Throws<ArgumentException>(() => new PdfPigTextExtractor().ExtractText([]));

    [Fact]
    public void Null_bytes_throw_argument_exception()
        => Assert.Throws<ArgumentException>(() => new PdfPigTextExtractor().ExtractText(null!));
}
