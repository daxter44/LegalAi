using UglyToad.PdfPig;

namespace PrawoRAG.Api.Services;

/// <summary>Wynik ekstrakcji załącznika PDF: tekst per strona + flagi jakości (DOC-0).</summary>
public sealed record PdfText(IReadOnlyList<string> Pages, bool Truncated)
{
    public int PageCount => Pages.Count;

    /// <summary>Heurystyka skanu: born-digital ma setki–tysiące znaków/stronę; skan bez warstwy
    /// tekstowej — zero lub artefakty. PdfPig nie robi OCR, więc skan = uczciwa odmowa u callera
    /// (filozofia jak przy abstynencji: odmowa zamiast udawania, że przeczytaliśmy).</summary>
    public bool IsScanLike =>
        Pages.Count == 0 || Pages.Average(p => p.Length) < PdfAttachmentExtractor.MinCharsPerPage;
}

/// <summary>
/// Ekstrakcja tekstu z załącznika użytkownika (umowa, pismo, wyrok) — osobna od ingestii ELI
/// (tam kwarantanna dokumentu korpusu; tu natychmiastowy feedback w UI). PdfPig: czysto zarządzany,
/// bez OCR. Wołający MUSI łapać wyjątki (uszkodzony/złośliwy plik) i pokazać czytelny komunikat —
/// treść załącznika nigdy nie jest persystowana (decyzja #1 planu DOC).
/// </summary>
public static class PdfAttachmentExtractor
{
    /// <summary>Twarde limity wejścia (decyzja #6): chronią pamięć obwodu i budżet TEI.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxPages = 100;

    /// <summary>Próg bramki skanów: średnio mniej znaków/stronę → PDF bez użytecznej warstwy tekstowej.</summary>
    public const int MinCharsPerPage = 200;

    public static PdfText Extract(byte[] pdf, int maxPages = MaxPages)
    {
        if (pdf is null || pdf.Length == 0)
            throw new ArgumentException("Pusty plik — brak bajtów do ekstrakcji.", nameof(pdf));

        var pages = new List<string>();
        var truncated = false;
        using (var doc = PdfDocument.Open(pdf))
        {
            foreach (var page in doc.GetPages())
            {
                if (pages.Count >= maxPages) { truncated = true; break; }
                pages.Add(page.Text);
            }
        }
        return new PdfText(pages, truncated);
    }
}
