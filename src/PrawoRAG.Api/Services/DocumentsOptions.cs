namespace PrawoRAG.Api.Services;

/// <summary>
/// Przełącznik funkcji załączników (upload PDF do kontekstu odpowiedzi). <see cref="Enabled"/>=false
/// (domyślnie) chowa afordancję w UI i czyni ścieżkę dokumentu martwą w <c>ChatService</c> — bez
/// usuwania kodu (klasy budulcowe zostają uśpione). MVP: wyłączone, bo pojedynczy prompt na cały plik
/// daje jedną zbiorczą odpowiedź zamiast analizy punkt-po-punkcie (psuje wrażenie jakości). Włączyć
/// dopiero po właściwej analizie per-punkt (bliższa trybowi kazusu).
/// </summary>
public sealed class DocumentsOptions
{
    public const string SectionName = "Documents";

    public bool Enabled { get; set; }
}
