using System.Text;
using UglyToad.PdfPig;

namespace PrawoRAG.Ingestion.Pdf;

/// <summary>
/// Ekstraktor oparty o PdfPig (czysto zarządzany, bez zależności natywnych — działa na laptopie, M4 i GCP).
/// <c>Page.Text</c> odtwarza tekst strony w kolejności zapisu w PDF; dla tekstów jednolitych Dz.U.
/// (born-digital) daje czytelny, poprawny tekst (zweryfikowane: art. 37 KK „30 lat"). Pusty wynik
/// (skan bez tekstu) traktujemy jak błąd — akt trafi do kwarantanny.
/// </summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public string ExtractText(byte[] pdf)
    {
        if (pdf is null || pdf.Length == 0)
            throw new ArgumentException("Pusty PDF — brak bajtów do ekstrakcji.", nameof(pdf));

        var sb = new StringBuilder();
        using (var doc = PdfDocument.Open(pdf))
        {
            foreach (var page in doc.GetPages())
            {
                sb.Append(page.Text);
                sb.Append('\n');
            }
        }

        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("PDF bez warstwy tekstowej (skan?) — ekstrakcja zwróciła pusty tekst.");
        return text;
    }
}
