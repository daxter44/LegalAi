using PrawoRAG.Api.Services;
using PrawoRAG.Tests.Fakes;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PrawoRAG.Tests.Documents;

/// <summary>T-DOC-0 — ekstraktor załączników PDF: born-digital → tekst per strona; bramka skanów;
/// limit stron z flagą obcięcia; puste bajty → jasny wyjątek (UI pokazuje komunikat).</summary>
public class PdfAttachmentExtractorTests
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
    public void Extracts_pages_separately()
    {
        var pdf = PdfAttachmentExtractor.Extract(BuildPdf("Strona pierwsza umowy", "Strona druga umowy"));
        Assert.Equal(2, pdf.PageCount);
        Assert.Contains("pierwsza", pdf.Pages[0]);
        Assert.Contains("druga", pdf.Pages[1]);
        Assert.False(pdf.Truncated);
    }

    [Fact] // krótki tekst na stronach → wygląda jak skan (bramka jakości)
    public void Short_pages_flagged_scan_like()
    {
        var pdf = PdfAttachmentExtractor.Extract(BuildPdf("x", "y"));
        Assert.True(pdf.IsScanLike);
    }

    [Fact] // realna gęstość tekstu → NIE skan
    public void Dense_pages_not_scan_like()
    {
        var longText = string.Join(" ", Enumerable.Repeat("paragraf umowy o swiadczenie uslug dla stron", 20));
        var pdf = PdfAttachmentExtractor.Extract(BuildPdf(longText));
        Assert.False(pdf.IsScanLike);
    }

    [Fact] // limit stron: nadmiar obcięty + flaga (uczciwa informacja w UI)
    public void Page_limit_truncates_with_flag()
    {
        var pdf = PdfAttachmentExtractor.Extract(BuildPdf("a", "b", "c", "d"), maxPages: 2);
        Assert.Equal(2, pdf.PageCount);
        Assert.True(pdf.Truncated);
    }

    [Fact]
    public void Empty_bytes_throw()
        => Assert.Throws<ArgumentException>(() => PdfAttachmentExtractor.Extract([]));
}

/// <summary>T-DOC-1a — chunker załączników (czysty, znakowy): pakowanie akapitów do limitu,
/// twardy podział przydługiego akapitu na granicy słowa, odsiew pustych linii.</summary>
public class DocChunkerTests
{
    [Fact]
    public void Packs_paragraphs_up_to_limit()
    {
        var chunks = DocChunker.Split(["akapit pierwszy\nakapit drugi", "akapit trzeci"], maxChars: 32);
        Assert.Equal(2, chunks.Count); // 1+2 mieszczą się razem (31 zn), trzeci osobno
        Assert.Equal("akapit pierwszy\nakapit drugi", chunks[0]);
        Assert.Equal("akapit trzeci", chunks[1]);
    }

    [Fact]
    public void Oversize_paragraph_split_at_word_boundary()
    {
        var chunks = DocChunker.Split([string.Join(" ", Enumerable.Repeat("słowo", 50))], maxChars: 60);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 60));
        Assert.All(chunks, c => Assert.DoesNotContain("słow\n", c)); // brak cięcia w środku słowa
    }

    [Fact]
    public void Empty_pages_yield_no_chunks()
        => Assert.Empty(DocChunker.Split(["", "  \n  "]));
}

/// <summary>T-DOC-1b — doc-retrieval in-memory: top-K po cosine, wynik w KOLEJNOŚCI DOKUMENTU,
/// przenumerowany [D1..K]. Embeddingi ręczne (wektory jednostkowe) — pełny determinizm.</summary>
public class DocumentContextTests
{
    private static DocumentContext Ctx(params (string Text, float[] Vec)[] chunks) => new()
    {
        FileName = "umowa.pdf", PageCount = 1, Truncated = false,
        Chunks = chunks.Select(c => c.Text).ToList(),
        Embeddings = chunks.Select(c => c.Vec).ToList(),
    };

    [Fact]
    public void Selects_most_similar_in_document_order()
    {
        // Zapytanie = oś X; chunk#0 prostopadły (sim 0), #1 idealny (sim 1), #2 skośny (sim ~0.7).
        var ctx = Ctx(("prostopadły", [0f, 1f]), ("idealny", [1f, 0f]), ("skośny", [1f, 1f]));

        var picked = ctx.SelectFragments([1f, 0f], topK: 2);

        Assert.Equal(2, picked.Count);
        Assert.Equal(["idealny", "skośny"], picked.Select(f => f.Text)); // kolejność dokumentu (1 przed 2)
        Assert.Equal([1, 2], picked.Select(f => f.Index));               // przenumerowane [D1],[D2]
    }

    [Fact]
    public void Empty_document_returns_no_fragments()
        => Assert.Empty(Ctx().SelectFragments([1f, 0f]));

    [Fact]
    public async Task CreateAsync_builds_chunks_and_embeddings()
    {
        var text = string.Join(" ", Enumerable.Repeat("postanowienie umowy najmu lokalu", 30));
        var ctx = await DocumentContext.CreateAsync(
            "umowa.pdf", new PdfText([text], Truncated: false), new FakeEmbeddingProvider(), default);

        Assert.Equal("umowa.pdf", ctx.FileName);
        Assert.True(ctx.Chunks.Count > 0);
        Assert.Equal(ctx.Chunks.Count, ctx.Embeddings.Count); // embedding per chunk
    }
}
