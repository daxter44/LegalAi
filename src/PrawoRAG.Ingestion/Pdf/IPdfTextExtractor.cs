namespace PrawoRAG.Ingestion.Pdf;

/// <summary>
/// Wyciąga warstwę tekstową z PDF (born-digital, BEZ OCR). Teksty jednolite Dz.U. od ~2012 są generowane
/// z XML i mają czystą warstwę tekstową — od stycznia 2025 ELI publikuje nowe akty i najnowsze teksty
/// jednolite wyłącznie w PDF (koniec HTML), więc to jedyna droga do aktualnego prawa rdzeniowego.
/// </summary>
public interface IPdfTextExtractor
{
    /// <summary>Zwraca tekst dokumentu (strony sklejone znakiem nowej linii). Rzuca, gdy PDF jest nieczytelny
    /// lub bez warstwy tekstowej (skan) — wołający decyduje o kwarantannie.</summary>
    string ExtractText(byte[] pdf);
}
