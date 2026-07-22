namespace PrawoRAG.Ingestion;

/// <summary>
/// Odporność fazy „process" na masowym korpusie (plan ODP, docs/PLAN-ODPORNOSC-INGESTU.md):
/// wznowienie po awarii ma kosztować minuty, a awaria infrastruktury ma przerywać run,
/// zamiast oznaczać Failed każdy kolejny dokument do końca magazynu.
/// </summary>
public sealed class ProcessOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Hurtowe pomijanie już zaindeksowanych dokumentów: jedno zapytanie na starcie runa
    /// (zbiór ExternalId+ContentHash) zamiast roundtripu do bazy per dokument.
    /// Kill-switch awaryjny — semantyka pominięcia jest identyczna z pipeline'em.
    /// </summary>
    public bool FastSkip { get; set; } = true;

    /// <summary>
    /// Ile porażek Z RZĘDU przerywa run. Seria (default 10) wskazuje awarię infrastruktury
    /// (TEI/DB/sieć), nie złe dokumenty — pojedyncze porażki nie przerywają, licznik zeruje
    /// każdy inny wynik. 0 = bezpiecznik wyłączony.
    /// </summary>
    public int FailStreakLimit { get; set; } = 10;

    /// <summary>Katalog raportów porażek JSONL (plik tworzony leniwie przy pierwszej porażce).</summary>
    public string FailureLogDir { get; set; } = "logs";

    /// <summary>
    /// Ile dokumentów przetwarzać RÓWNOLEGLE w fazie process (RÓWN-1). Domyślnie 1 = zachowanie
    /// sekwencyjne jak dotąd (zero zmian semantyki, w tym uporządkowanego bezpiecznika ODP-2).
    /// Podniesienie amortyzuje latencję LAN (M4↔Dell: gdy jeden dokument czeka na TEI/DB, inne liczą)
    /// i zapełnia kolejkę GPU — przy masowym korpusie to główna dźwignia przepustowości. Rozsądny
    /// start dla M4: 8. Uwaga: przy >1 „porażki z rzędu" bezpiecznika stają się przybliżeniem
    /// (współbieżne wątki), ale intencja (seria = awaria infrastruktury → przerwij) zostaje zachowana.
    /// </summary>
    public int ProcessParallelism { get; set; } = 1;
}
