namespace PrawoRAG.Ingestion.Chunking;

public sealed class ChunkerOptions
{
    public const string SectionName = "Chunker";

    /// <summary>Docelowy rozmiar chunka w tokenach.</summary>
    public int TargetTokens { get; set; } = 450;

    /// <summary>Twardy limit (≤ limit modelu mmlw = 512).</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>Zakładka między sąsiednimi chunkami (tokeny) — ciągłość kontekstu.</summary>
    public int OverlapTokens { get; set; } = 80;

    /// <summary>Heurystyka znaków/token (PL ~5) do wstępnego dzielenia bardzo długich akapitów przed liczeniem tokenów.</summary>
    public int ApproxCharsPerToken { get; set; } = 5;

    /// <summary>
    /// Minimalna liczba „sensownych słów" (≥3 litery) w chunku. Poniżej progu chunk jest odrzucany —
    /// to zdegenerowane fragmenty (checkboxy formularza, „⚫", pojedyncze linie, urwane słowa), które
    /// mają anomalnie wysokie cosine do KAŻDEGO zapytania i wypychają realne przepisy z top-K.
    /// Akty ELI mają ≥6 słów dzięki nagłówkowi kontekstowemu, więc próg 5 ich nie tyka.
    /// </summary>
    public int MinSubstantiveWords { get; set; } = 5;
}
