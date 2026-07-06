namespace PrawoRAG.Domain;

/// <summary>
/// Klucze źródeł danych. Dodanie nowego źródła = nowa stała + implementacja <see cref="Sources.ISourceConnector"/>.
/// Trzymamy stringi (nie enum), by rejestr źródeł był otwarty na rozszerzenia bez zmian w rdzeniu.
/// </summary>
public static class SourceKeys
{
    public const string Saos = "SAOS";
    public const string Eli = "ELI";
}

/// <summary>
/// Format głównej treści <see cref="Sources.RawDocument.RawContent"/> — normalizer wybiera ścieżkę parsowania.
/// ELI od stycznia 2025 przestał publikować HTML: nowe akty i najnowsze teksty jednolite (też kodeksów)
/// są tylko w PDF, więc aktualne prawo wymaga ścieżki PDF obok HTML.
/// </summary>
public static class ContentFormats
{
    /// <summary>Strukturalny HTML (ELI <c>text.html</c>, SAOS <c>textContent</c>) — parsowanie po węzłach DOM.</summary>
    public const string Html = "html";

    /// <summary>Płaski tekst wyekstrahowany z PDF (tekst jednolity ELI) — parsowanie po znacznikach „Art."/„§".</summary>
    public const string PdfText = "pdf-text";
}

/// <summary>Typy dokumentów — sterują wyborem normalizera i kształtem metadanych/lokalizatora cytatu.</summary>
public static class DocTypes
{
    public const string Judgment = "judgment"; // orzeczenie
    public const string Act = "act";           // akt prawny
    public const string Article = "article";   // artykuł prawniczy (po MVP)
    public const string Book = "book";         // książka OCR .md (po MVP)
}

/// <summary>
/// Maszyna stanów dokumentu w pipeline ingestii (sekcja „Idempotencja i wznawialność ingestu" w planie).
/// Pozwala wznowić przetwarzanie i pomijać dokumenty już zaindeksowane.
/// </summary>
public enum DocumentStatus
{
    Discovered = 0,
    Fetched = 1,
    Normalized = 2,
    Chunked = 3,
    Embedded = 4,
    Indexed = 5,
    Failed = 6,
}
