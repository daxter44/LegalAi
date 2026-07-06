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

    /// <summary>Odkrywanie aktów z list roczników ELI (zamiast ręcznej listy adresów).</summary>
    public EliDiscoverOptions Discover { get; set; } = new();
}

/// <summary>
/// Konfiguracja odkrywania: enumeruje listy roczników (<c>/eli/acts/{publisher}/{year}</c>) i wybiera akty
/// po typie + statusie „obowiązujący" + dostępności tekstu HTML. Adresy zasilają pobieranie (obok <c>Acts</c>).
/// </summary>
public sealed class EliDiscoverOptions
{
    public bool Enabled { get; set; }

    /// <summary>Wydawca: „DU" (Dziennik Ustaw) lub „MP" (Monitor Polski).</summary>
    public string Publisher { get; set; } = "DU";

    public int YearFrom { get; set; } = 1994;
    public int YearTo { get; set; } = 2025;

    /// <summary>Typy aktów do wzięcia (dokładne etykiety z API): „Ustawa", „Rozporządzenie", …</summary>
    public List<string> Types { get; set; } = ["Ustawa", "Rozporządzenie"];

    /// <summary>
    /// Akceptowane statusy (pusta lista = dowolny). Domyślnie akty żywe z AKTUALNYM tekstem HTML:
    /// „obowiązujący" (w mocy, bez konsolidacji) + „akt posiada tekst jednolity" (w mocy; jego text.html
    /// to skonsolidowany, bieżący tekst — np. kodeksy). Świadomie POMIJAMY „akt objęty tekstem jednolitym"
    /// (akty nowelizujące wchłonięte do tekstu bazowego — niska wartość samodzielna, ryzyko dubli/starej treści).
    /// </summary>
    public List<string> Statuses { get; set; } = ["obowiązujący", "akt posiada tekst jednolity"];
}
