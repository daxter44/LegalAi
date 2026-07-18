using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// JAK-0/1 (`--sanitize-chunks`): pomiar i neutralizacja chunków zdegenerowanych (placeholdery
/// „(pominięty)"/„(uchylony)" + szum anonimizacyjny SAOS — patrz <see cref="ChunkDegeneracy"/>).
///
/// DRY-RUN (domyślnie): liczy kandydatów per kategoria, wypisuje losową próbkę do przejrzenia
/// okiem i zapisuje PEŁNĄ listę Id+tekst do logs/sanitize-*.jsonl (to jednocześnie ścieżka
/// powrotu — lista dokładnie tego, co --apply wyzeruje). Niczego nie zmienia.
///
/// APPLY (`--apply`): UPDATE "Embedding"=NULL dla zakwalifikowanych → znikają z toru gęstego
/// (WHERE Embedding IS NOT NULL już jest w retrieverze), bez migracji i bez reprocessingu.
/// Odwracalne: re-embedding przez pipeline (Embedding IS NULL) albo z listy z dry-runa.
/// Tor BM25 świadomie NIEfiltrowany w tej iteracji (śmieci wygrywały semantycznie, nie
/// leksykalnie) — zmierzyć po fakcie, czy wystarczy.
///
/// Prefiltr kandydatów w SQL (LIKE po wzorcach), klasyfikacja właściwa w C# — pełny skan
/// 7,4M tekstów przez LAN byłby wielokrotnie droższy niż seq-scan LIKE po stronie bazy.
/// </summary>
public static class SanitizeChunksRunner
{
    private sealed record Candidate(Guid Id, string Category, string TextPreview);

    public static async Task RunAsync(IServiceProvider services, IConfiguration cfg, string[] args, CancellationToken ct)
    {
        var apply = args.Contains("--apply");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30)); // seq-scan LIKE po 7,4M — jednorazowo wolno, to OK

        Console.WriteLine($"Sanityzacja chunków — tryb: {(apply ? "APPLY (zeruję embeddingi!)" : "DRY-RUN (tylko raport)")}.\n");

        // Prefiltr: wzorce obu kategorii. ESCAPE dla LIKE nie jest potrzebny — kropki i nawiasy
        // w LIKE są literalne; % tylko nasze.
        Console.WriteLine("Prefiltr SQL (seq-scan LIKE po pełnym korpusie — to może potrwać kilka minut)...");
        var candidates = await db.Chunks.AsNoTracking()
            .Where(c => c.Embedding != null &&
                (EF.Functions.ILike(c.Text, "%(pominięt%") ||
                 EF.Functions.ILike(c.Text, "%(uchylon%") ||
                 EF.Functions.ILike(c.Text, "%(skreślon%") ||
                 c.Text.Contains("(...)") || c.Text.Contains("(…)")))
            .Select(c => new { c.Id, c.Text })
            .ToListAsync(ct);
        Console.WriteLine($"Kandydatów po prefiltrze: {candidates.Count}.");

        // Klasyfikacja właściwa (czysty detektor — te same reguły wejdą do chunkera w JAK-2).
        var qualified = candidates
            .Select(c => new Candidate(
                c.Id,
                ChunkDegeneracy.IsOmittedPlaceholder(c.Text) ? "placeholder"
                    : ChunkDegeneracy.IsAnonymizationNoise(c.Text) ? "anonimizacja"
                    : "ok",
                Preview(c.Text)))
            .Where(c => c.Category != "ok")
            .ToList();

        Console.WriteLine($"Zakwalifikowanych do neutralizacji: {qualified.Count} " +
                          $"(placeholder={qualified.Count(q => q.Category == "placeholder")}, " +
                          $"anonimizacja={qualified.Count(q => q.Category == "anonimizacja")}); " +
                          $"odrzuconych przez detektor (prefiltr złapał, treść realna): {candidates.Count - qualified.Count}.\n");

        // Pełna lista do pliku — raport + ścieżka powrotu dla --apply.
        Directory.CreateDirectory("logs");
        var path = Path.Combine("logs", $"sanitize-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        await using (var w = new StreamWriter(path))
            foreach (var q in qualified)
                await w.WriteLineAsync(JsonSerializer.Serialize(q));
        Console.WriteLine($"Pełna lista: {path}");

        // Próbka do przejrzenia okiem (deterministyczny „random": co N-ty po posortowaniu — powtarzalne).
        Console.WriteLine("\n=== PRÓBKA 50 (oceń okiem, czy nie wycinamy niczego wartościowego) ===");
        var ordered = qualified.OrderBy(q => q.Id).ToList();
        var step = Math.Max(1, ordered.Count / 50);
        foreach (var q in ordered.Where((_, i) => i % step == 0).Take(50))
            Console.WriteLine($"  [{q.Category,-12}] {q.TextPreview}");

        if (!apply)
        {
            Console.WriteLine("\nDRY-RUN zakończony. Po akceptacji próbki: to samo polecenie z --apply.");
            return;
        }

        Console.WriteLine("\nZeruję embeddingi (partiami po 1000)...");
        var ids = qualified.Select(q => q.Id).ToList();
        var total = 0;
        foreach (var batch in ids.Chunk(1000))
        {
            total += await db.Chunks.Where(c => batch.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Embedding, (Pgvector.Vector?)null), ct);
            Console.Write($"\r  {total}/{ids.Count}");
        }
        Console.WriteLine($"\nGotowe: {total} chunków zniknęło z toru gęstego. " +
                          $"Odwrót: lista w {path}; re-embedding uzupełni pipeline (Embedding IS NULL).");
    }

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
