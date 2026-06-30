namespace PrawoRAG.Ingestion.Saos;

/// <summary>Konfiguracja konektora SAOS — w tym wycinek korpusu MVP (0.6).</summary>
public sealed class SaosOptions
{
    public const string SectionName = "Saos";

    public string BaseUrl { get; set; } = "https://www.saos.org.pl/api";

    /// <summary>Typ sądu wycinka (COMMON jest jedynym aktualizowanym — patrz „świeżość SAOS" w planie).</summary>
    public string CourtType { get; set; } = "COMMON";

    /// <summary>Poziom sądu powszechnego: APPEAL/REGIONAL/DISTRICT (null = wszystkie).</summary>
    public string? CcCourtType { get; set; } = "APPEAL";

    /// <summary>Dolna granica daty orzeczenia (wycinek). Format yyyy-MM-dd.</summary>
    public string JudgmentDateFrom { get; set; } = "2023-01-01";

    public int PageSize { get; set; } = 100;

    /// <summary>Limit na pojedynczą próbę HTTP (s). Search SAOS z filtrami (COMMON/APPEAL + sort) liczy
    /// po stronie serwera ~8–15s (wariancja niezależna od pageSize) — domyślne 10s resilience handlera
    /// to za mało i ubija każdą próbę. 45s daje zapas nad obserwowanym maksimum.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 45;
}
