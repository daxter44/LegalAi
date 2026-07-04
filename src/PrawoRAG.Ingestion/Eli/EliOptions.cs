namespace PrawoRAG.Ingestion.Eli;

/// <summary>Konfiguracja konektora ELI/Sejm — lista aktów do pobrania (adresy ELI).</summary>
public sealed class EliOptions
{
    public const string SectionName = "Eli";

    public string BaseUrl { get; set; } = "https://api.sejm.gov.pl/eli";

    /// <summary>Adresy aktów do ingestii, np. „DU/1997/553" (KK). Lista otwarta — dokładamy kolejne.</summary>
    public List<string> Acts { get; set; } = [];

    /// <summary>Limit na pojedynczą próbę HTTP (s) — text.html kodeksów bywa duży (KK ~1 MB).</summary>
    public int AttemptTimeoutSeconds { get; set; } = 45;
}
