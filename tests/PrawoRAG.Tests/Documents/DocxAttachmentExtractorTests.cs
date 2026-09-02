using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PrawoRAG.Api.Services;
using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Documents;

/// <summary>AJ-11 — ekstraktor DOCX: akapity jako osobne linie (splitter trafia nagłówki § trybem
/// „początek linii"), tabele wiersz po wierszu, pakowanie w „strony", pusty/uszkodzony plik →
/// czytelny błąd, dispatcher po rozszerzeniu.</summary>
public class DocxAttachmentExtractorTests
{
    private static byte[] BuildDocx(IEnumerable<string> paragraphs, IEnumerable<string[]>? tableRows = null)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var p in paragraphs)
            {
                // Word rozbija zdanie na kilka <w:r> przy zmianie formatowania — symulujemy to.
                var para = new Paragraph();
                var half = p.Length / 2;
                para.Append(new Run(new Text(p[..half]) { Space = SpaceProcessingModeValues.Preserve }));
                para.Append(new Run(new RunProperties(new Bold()), new Text(p[half..]) { Space = SpaceProcessingModeValues.Preserve }));
                body.Append(para);
            }
            if (tableRows is not null)
            {
                var table = new Table();
                foreach (var cells in tableRows)
                {
                    var row = new TableRow();
                    foreach (var c in cells)
                        row.Append(new TableCell(new Paragraph(new Run(new Text(c)))));
                    table.Append(row);
                }
                body.Append(table);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static readonly string[] Contract =
    [
        "UMOWA NAJMU LOKALU MIESZKALNEGO zawarta w dniu 1 sierpnia 2026 r. pomiędzy Janem Kowalskim a Anną Nowak.",
        "§1. Przedmiot najmu. Wynajmujący oddaje Najemcy do używania lokal mieszkalny nr 4 przy ul. Kwiatowej 15 w Poznaniu.",
        "§2. Czynsz. Czynsz najmu wynosi 3 200 zł miesięcznie, płatny z góry do 10. dnia każdego miesiąca na rachunek Wynajmującego.",
        "§3. Kaucja. Najemca wpłaca kaucję zabezpieczającą w wysokości 80 000 zł, płatną przed wydaniem lokalu. Kaucja nie podlega oprocentowaniu.",
    ];

    [Fact]
    public void Paragraphs_become_lines_and_runs_are_joined()
    {
        var text = DocxAttachmentExtractor.Extract(BuildDocx(Contract));
        Assert.Single(text.Pages);
        var lines = text.Pages[0].Split('\n');
        Assert.Equal(Contract, lines);
        Assert.False(text.IsScanLike);
        Assert.False(text.Truncated);
    }

    [Fact]
    public void Splitter_finds_section_headings_from_docx_lines()
    {
        var text = DocxAttachmentExtractor.Extract(BuildDocx(Contract));
        var units = LegalUnitSplitter.Split(text.Pages);
        Assert.Equal(["wstęp", "§ 1", "§ 2", "§ 3"], units.Select(u => u.Heading));
    }

    [Fact]
    public void Table_rows_become_tab_separated_lines()
    {
        var text = DocxAttachmentExtractor.Extract(BuildDocx(
            ["Załącznik nr 1 — wykaz wyposażenia lokalu przekazanego Najemcy w dniu wydania."],
            [["Lodówka", "1 szt."], ["Pralka", "1 szt."]]));
        var lines = text.Pages[0].Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Lodówka\t1 szt.", lines[1]);
    }

    [Fact]
    public void Long_document_is_packed_into_pages_and_truncated_with_flag()
    {
        var para = new string('x', 700) + ".";
        var many = Enumerable.Repeat(para, 20); // 20 × 700 zn ≈ 5 „stron" po 3000
        var text = DocxAttachmentExtractor.Extract(BuildDocx(many));
        Assert.True(text.PageCount >= 4 && text.PageCount <= 6, $"stron: {text.PageCount}");
        Assert.All(text.Pages, p => Assert.True(p.Length <= DocxAttachmentExtractor.PageChars + 1));

        var cut = DocxAttachmentExtractor.Extract(BuildDocx(many), maxPages: 2);
        Assert.Equal(2, cut.PageCount);
        Assert.True(cut.Truncated);
    }

    [Fact]
    public void Empty_document_is_scan_like_not_exception()
    {
        var text = DocxAttachmentExtractor.Extract(BuildDocx([]));
        Assert.True(text.IsScanLike);
    }

    [Fact]
    public void Garbage_bytes_throw_clear_error()
    {
        Assert.Throws<ArgumentException>(() => DocxAttachmentExtractor.Extract([]));
        Assert.ThrowsAny<Exception>(() => DocxAttachmentExtractor.Extract([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void Dispatcher_routes_by_extension()
    {
        Assert.True(AttachmentExtractor.IsSupported("umowa.DOCX"));
        Assert.True(AttachmentExtractor.IsSupported("umowa.pdf"));
        Assert.False(AttachmentExtractor.IsSupported("umowa.doc"));
        Assert.Equal("DOCX", AttachmentExtractor.Kind("a.docx"));
        Assert.Null(AttachmentExtractor.Kind("a.rtf"));
        Assert.Throws<NotSupportedException>(() => AttachmentExtractor.Extract("a.rtf", [1]));
        Assert.Equal(4, AttachmentExtractor.Extract("umowa.docx", BuildDocx(Contract)).Pages[0].Split('\n').Length);
    }
}
