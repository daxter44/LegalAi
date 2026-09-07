using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Wspólne wejście ekstrakcji załączników (AJ-11): wybór ekstraktora po rozszerzeniu pliku.
/// PDF → <see cref="PdfAttachmentExtractor"/> (PdfPig, bramka skanów), DOCX →
/// <see cref="DocxAttachmentExtractor"/> (OpenXML, zachowane akapity). Oba zwracają ten sam
/// <see cref="AttachmentText"/>, więc splitter, chunker i UI nie wiedzą, skąd przyszedł tekst.
/// Limity (rozmiar, strony) i zasada „treść nigdy nie jest persystowana" są wspólne.
/// </summary>
public static class AttachmentExtractor
{
    public const long MaxBytes = PdfAttachmentExtractor.MaxBytes;

    /// <summary>Wartość atrybutu <c>accept</c> dla <c>InputFile</c> — jedno źródło prawdy dla obu stron.</summary>
    public const string Accept = ".pdf,.docx";

    public static readonly IReadOnlyList<string> SupportedExtensions = [".pdf", ".docx"];

    public static bool IsSupported(string fileName) =>
        SupportedExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>„PDF" / „DOCX" do komunikatów UI; null = nieobsługiwane rozszerzenie.</summary>
    public static string? Kind(string fileName) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF"
        : fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? "DOCX"
        : null;

    /// <summary>Wołający MUSI łapać wyjątki (uszkodzony/zabezpieczony plik) — jak przy PDF.</summary>
    public static AttachmentText Extract(string fileName, byte[] bytes) => Kind(fileName) switch
    {
        "PDF" => PdfAttachmentExtractor.Extract(bytes),
        "DOCX" => DocxAttachmentExtractor.Extract(bytes),
        _ => throw new NotSupportedException($"Nieobsługiwany format załącznika: {fileName}"),
    };
}

/// <summary>
/// Ekstrakcja tekstu z DOCX (AJ-11). Umowy powstają w Wordzie — wymuszanie „zapisz jako PDF" było
/// tarciem przy każdym użyciu, a PdfPig gubi łamania linii, przez co splitter musiał zgadywać
/// nagłówki „płaskim" trybem. Tu każdy akapit Worda to osobna linia, więc <c>LegalUnitSplitter</c>
/// trafia w nagłówki § / art. najpewniejszą strategią (początek linii).
/// „Strony" DOCX nie istnieją w modelu OpenXML (paginacja to sprawa renderera) — pakujemy akapity
/// w porcje ~<see cref="PageChars"/> znaków, żeby <see cref="AttachmentText.PageCount"/> i limit
/// stron znaczyły w UI mniej więcej to samo co dla PDF. Tabele czytane wiersz po wierszu
/// (komórki łączone tabulatorem). Nagłówki/stopki sekcji pomijane (numeracja stron, logo).
/// </summary>
public static class DocxAttachmentExtractor
{
    /// <summary>Orientacyjna „strona" DOCX w znakach (≈ strona A4 gęstego tekstu prawniczego).</summary>
    public const int PageChars = 3000;

    public static AttachmentText Extract(byte[] docx, int maxPages = PdfAttachmentExtractor.MaxPages)
    {
        if (docx is null || docx.Length == 0)
            throw new ArgumentException("Pusty plik — brak bajtów do ekstrakcji.", nameof(docx));

        var lines = new List<string>();
        using (var ms = new MemoryStream(docx, writable: false))
        using (var doc = WordprocessingDocument.Open(ms, isEditable: false))
        {
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("DOCX bez treści dokumentu (brak document.xml/body).");
            foreach (var element in body.ChildElements)
                AppendBlock(element, lines);
        }

        var pages = new List<string>();
        var truncated = false;
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > PageChars)
            {
                if (pages.Count >= maxPages) { truncated = true; break; }
                pages.Add(sb.ToString().TrimEnd());
                sb.Clear();
            }
            sb.Append(line).Append('\n');
        }
        if (!truncated && sb.Length > 0)
        {
            if (pages.Count >= maxPages) truncated = true;
            else pages.Add(sb.ToString().TrimEnd());
        }
        return new AttachmentText(pages, truncated);
    }

    /// <summary>Akapit → jedna linia (puste akapity pomijane); tabela → wiersz per linia; inne
    /// bloki (sekcje, zakładki) ignorowane. Treść zagnieżdżona (np. tabela w komórce) spłaszczona.</summary>
    private static void AppendBlock(DocumentFormat.OpenXml.OpenXmlElement element, List<string> lines)
    {
        switch (element)
        {
            case Paragraph p:
                var text = ParagraphText(p);
                if (text.Length > 0) lines.Add(text);
                break;
            case Table t:
                foreach (var row in t.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>()
                        .Select(c => string.Join(" ", c.Elements<Paragraph>().Select(ParagraphText).Where(s => s.Length > 0)))
                        .Where(s => s.Length > 0);
                    var line = string.Join("\t", cells);
                    if (line.Length > 0) lines.Add(line);
                }
                break;
            case SdtBlock sdt: // kontrolka treści — zaglądamy do środka
                foreach (var child in sdt.SdtContentBlock?.ChildElements ?? [])
                    AppendBlock(child, lines);
                break;
        }
    }

    /// <summary>Tekst akapitu z uwzględnieniem twardych łamań (<c>w:br</c>) i tabulatorów — Word
    /// rozbija zdanie na wiele <c>w:r</c> przy każdej zmianie formatowania, więc składamy wszystko.</summary>
    private static string ParagraphText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case Text t: sb.Append(t.Text); break;
                case TabChar: sb.Append('\t'); break;
                case Break: sb.Append('\n'); break;
            }
        }
        return NormalizeWhitespace(sb.ToString());
    }

    private static string NormalizeWhitespace(string s)
    {
        // Zachowaj łamania z <w:br>, zbij pozostałe białe znaki do jednej spacji.
        var parts = s.Split('\n').Select(part => string.Join(" ", part.Split([' ', '\t', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)));
        return string.Join("\n", parts).Trim();
    }
}
