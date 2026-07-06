using Microsoft.Extensions.Logging;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion;

/// <summary>
/// Raport jakości NORMALIZACJI (bez embeddingu, bez bazy — czysty CPU, za darmo). Czyta surowe dokumenty
/// z magazynu, każdy normalizuje i wypisuje statystyki + próbkę tekstu. Służy do OCENY, czy parser radzi
/// sobie z danym typem (ustawa / rozporządzenie / orzeczenie danego poziomu) PRZED masowym embeddingiem.
/// </summary>
public sealed class QualityReportRunner(
    IRawDocumentStore store,
    IEnumerable<IDocumentNormalizer> normalizers,
    ILogger<QualityReportRunner> log)
{
    private readonly Dictionary<string, IDocumentNormalizer> _normalizers =
        normalizers.ToDictionary(n => n.DocType, StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(string source, int? maxItems, CancellationToken ct)
    {
        int docs = 0, failed = 0, empty = 0, withIssues = 0, totalSegments = 0;
        log.LogInformation("Raport jakości {Source} start", source);
        Console.WriteLine($"\n=== RAPORT JAKOŚCI NORMALIZACJI: {source} ===\n");

        await foreach (var raw in store.EnumerateAsync(source, ct))
        {
            if (maxItems is { } m && docs >= m) break;
            docs++;

            if (!_normalizers.TryGetValue(raw.DocType, out var normalizer))
            {
                Console.WriteLine($"[{raw.ExternalId}] ⚠ BRAK normalizera dla typu '{raw.DocType}'\n");
                failed++;
                continue;
            }

            NormalizedDocument nd;
            try { nd = normalizer.Normalize(raw); }
            catch (Exception ex)
            {
                Console.WriteLine($"[{raw.ExternalId}] ⚠ WYJĄTEK normalizacji: {ex.Message}\n");
                failed++;
                continue;
            }

            var segs = nd.Segments.Count;
            totalSegments += segs;
            if (segs == 0) empty++;
            if (nd.QualityIssues.Count > 0) withIssues++;

            var avgSeg = segs > 0 ? nd.Segments.Average(s => s.Text.Length) : 0;
            Console.WriteLine($"[{raw.ExternalId}] {Trunc(nd.Title, 70)}");
            Console.WriteLine($"    segmentów={segs}  tekst={nd.PlainText?.Length ?? 0} zn.  śr.segment={avgSeg:F0} zn.  issues={nd.QualityIssues.Count}");
            if (nd.QualityIssues.Count > 0)
                Console.WriteLine($"    ⚠ issues: {string.Join(" | ", nd.QualityIssues.Take(3))}");
            var first = nd.Segments.FirstOrDefault();
            if (first is not null)
                Console.WriteLine($"    próbka 1. segmentu: „{Trunc(Clean(first.Text), 200)}”");
            Console.WriteLine();
        }

        Console.WriteLine($"=== PODSUMOWANIE {source}: dok={docs}  bez_segmentów={empty}  z_issues={withIssues}  błędy={failed}  segmentów_łącznie={totalSegments} ===");
        if (empty > 0)
            Console.WriteLine($"⚠ {empty} dok. bez segmentów — parser NIE poradził sobie (do poprawy PRZED masowym pobraniem).");
        if (failed > 0)
            Console.WriteLine($"⚠ {failed} dok. z błędem/brakiem normalizera.");
        if (empty == 0 && failed == 0)
            Console.WriteLine("✓ Każdy dokument dał segmenty — parsowanie wygląda zdrowo (obejrzyj próbki, czy to czysty tekst).");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    private static string Clean(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
