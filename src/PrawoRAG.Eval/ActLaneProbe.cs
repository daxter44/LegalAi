using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// Sonda diagnostyczna `--probe-akty`: mierzy NA ŻYWEJ BAZIE, dlaczego przepis rządzący
/// (np. art. 415 KC) nie wchodzi do kontekstu pytań opisowych — i który wariant naprawy ma
/// pokrycie w danych, ZANIM powstanie kod w retrieverze. Cztery pomiary per pytanie:
///   A. sanity korpusu — czy artykuły-kandydaci w ogóle są w bazie (raz, nie per pytanie);
///   B. pełny dense top-50 — niezależna weryfikacja dowodów z sesji 2026-07-17
///      (rozkład typów dokumentów, pozycja pierwszego aktu);
///   C. act-only dense top-20 — czy w puli samych aktów wygrywa przepis WŁAŚCIWY (415),
///      czy leksykalnie podobny (149 — „drzewa", „grunt sąsiedni");
///   D. most cytowań (dry-run) — czy chunki top-orzeczeń same cytują właściwy przepis
///      („art. 415 k.c."), czyli czy ekstrakcja cytowań z trafień dałaby normę bez ML.
/// Tylko odczyt; zero zmian w retrieverze. Uruchamiać z maszyny widzącej bazę i TEI (M4).
/// </summary>
public static class ActLaneProbe
{
    private const int FullTopK = 50;
    private const int ActTopK = 20;
    private const int BridgeChunks = 30;
    private const int HnswEfSearch = 400; // parytet z HybridRetriever — inne ef_search = inny wynik

    /// <summary>Pytania domyślne — obie ścieżki z diagnozy 2026-07-17 (naturalna i normatywna).</summary>
    private static readonly string[] DefaultQuestions =
    [
        "Sąsiad twierdzi, że drzewo z mojej działki spadło na jego ogrodzenie i żąda odszkodowania. " +
        "Drzewo było zdrowe, przewróciła je wichura. Czy odpowiadam za taką szkodę?",
        "Odpowiedzialność za szkodę wyrządzoną z winy sprawcy a zwolnienie z odpowiedzialności przez siłę wyższą",
    ];

    /// <summary>Artykuły-kandydaci do sanity-check (sekcja A): właściwe (415, 361, 435) i pułapka (149).</summary>
    private static readonly (string Alias, string Article, string Rola)[] CandidateArticles =
    [
        ("KC", "415", "norma właściwa (delikt, wina)"),
        ("KC", "361", "norma właściwa (związek przyczynowy)"),
        ("KC", "435", "norma pokrewna (ryzyko/siła wyższa)"),
        ("KC", "149", "PUŁAPKA leksykalna (gałęzie na gruncie sąsiada)"),
    ];

    public static async Task RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        // Własne pytanie: wszystko po fladze, co nie jest flagą, sklejone w jedno.
        var custom = args.SkipWhile(a => a != "--probe-akty").Skip(1).Where(a => !a.StartsWith("--")).ToArray();
        var questions = custom.Length > 0 ? [string.Join(' ', custom)] : DefaultQuestions;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();

        Console.WriteLine("=== SONDA ACT-LANE — pomiary na żywej bazie (tylko odczyt) ===\n");

        await SanityCheckAsync(db, ct);

        foreach (var q in questions)
        {
            Console.WriteLine($"\n──────────────────────────────────────────────────────");
            Console.WriteLine($"PYTANIE: {q}\n");
            var qvec = new Vector(await embedder.EmbedQueryAsync(q, ct));

            // ef_search na tym samym połączeniu co zapytania dense (jak w HybridRetriever).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL hnsw.ef_search = {HnswEfSearch}", ct);

            var full = await DenseAsync(db, qvec, FullTopK, actsOnly: false, ct);
            await ReportFullDenseAsync(db, full, ct);

            var acts = await DenseAsync(db, qvec, ActTopK, actsOnly: true, ct);
            await ReportActLaneAsync(db, acts, ct);

            await ReportCitationBridgeAsync(db, full, ct);
        }
    }

    // ────────────────────────────── A. Sanity korpusu ──────────────────────────────

    private static async Task SanityCheckAsync(PrawoRagDbContext db, CancellationToken ct)
    {
        Console.WriteLine("A. SANITY KORPUSU — artykuły-kandydaci");
        foreach (var (alias, article, rola) in CandidateArticles)
        {
            var extId = await ResolveActAsync(db, alias, ct);
            if (extId is null) { Console.WriteLine($"   {alias} art. {article}: BRAK AKTU ({rola})"); continue; }

            var chunk = await db.Chunks.AsNoTracking()
                .Where(c => c.ArticleNo == article && c.Document!.ExternalId == extId)
                .OrderBy(c => c.ChunkIndex)
                .Select(c => new { c.TokenCount, c.Text, HasEmb = c.Embedding != null })
                .FirstOrDefaultAsync(ct);

            Console.WriteLine(chunk is null
                ? $"   {alias} art. {article}: BRAK CHUNKA ({rola}) — jeśli brak, żaden tor go nie znajdzie!"
                : $"   {alias} art. {article}: JEST, {chunk.TokenCount} tok, embedding={(chunk.HasEmb ? "tak" : "BRAK")} ({rola})\n" +
                  $"      „{Trim(chunk.Text, 140)}”");
        }
    }

    // ────────────────────────────── B. Pełny dense ──────────────────────────────

    private static async Task ReportFullDenseAsync(PrawoRagDbContext db, List<ProbeHit> hits, CancellationToken ct)
    {
        Console.WriteLine($"B. PEŁNY DENSE top-{FullTopK} (weryfikacja dowodów z sesji 07-17)");
        var byType = hits.GroupBy(h => h.DocType).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine($"   Rozkład typów: {string.Join(", ", byType)}");

        var firstAct = hits.FindIndex(h => h.DocType == "act");
        Console.WriteLine(firstAct < 0
            ? $"   Pierwszy AKT: nieobecny w top-{FullTopK} — potwierdza diagnozę „statut nieretrievalny”."
            : $"   Pierwszy AKT: pozycja #{firstAct + 1}, sim={1 - hits[firstAct].Dist:F4}");

        var detail = await DetailsAsync(db, hits.Take(15).Select(h => h.Id).ToList(), ct);
        foreach (var (h, i) in hits.Take(15).Select((h, i) => (h, i)))
        {
            var d = detail[h.Id];
            Console.WriteLine($"   #{i + 1,2} sim={1 - h.Dist:F4} [{h.DocType,-8}] {Trim(d.Label, 90)}");
        }
    }

    // ────────────────────────────── C. Act-only dense ──────────────────────────────

    private static async Task ReportActLaneAsync(PrawoRagDbContext db, List<ProbeHit> hits, CancellationToken ct)
    {
        Console.WriteLine($"\nC. ACT-ONLY DENSE top-{ActTopK} (kto wygrywa w puli samych aktów?)");
        if (hits.Count == 0) { Console.WriteLine("   (zero aktów z embeddingiem — patrz sekcja A)"); return; }

        var detail = await DetailsAsync(db, hits.Select(h => h.Id).ToList(), ct);
        foreach (var (h, i) in hits.Select((h, i) => (h, i)))
        {
            var d = detail[h.Id];
            var marker = d.Article == "415" ? "  ◄◄◄ WŁAŚCIWY" : d.Article == "149" ? "  ◄ pułapka" : "";
            Console.WriteLine($"   #{i + 1,2} sim={1 - h.Dist:F4} art. {d.Article ?? "?",-6} {Trim(d.Label, 80)}{marker}");
        }

        int Pos(string art) => hits.FindIndex(h => detail[h.Id].Article == art) + 1; // 0 = nieobecny
        Console.WriteLine($"   → art. 415: {(Pos("415") > 0 ? $"pozycja #{Pos("415")}" : "NIEOBECNY")}, " +
                          $"art. 149: {(Pos("149") > 0 ? $"pozycja #{Pos("149")}" : "nieobecny")}. " +
                          $"Jeśli 149 < 415 — prosty tor act-only NIE rozwiązuje problemu.");
    }

    // ────────────────────────────── D. Most cytowań ──────────────────────────────

    private static async Task ReportCitationBridgeAsync(PrawoRagDbContext db, List<ProbeHit> full, CancellationToken ct)
    {
        Console.WriteLine($"\nD. MOST CYTOWAŃ (dry-run): co cytują chunki top-{BridgeChunks} orzeczeń?");
        var judgmentIds = full.Where(h => h.DocType != "act").Take(BridgeChunks).Select(h => h.Id).ToList();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => judgmentIds.Contains(c.Id))
            .Select(c => new { c.Id, c.DocumentId, c.Text })
            .ToListAsync(ct);

        // Agregacja per (akt, artykuł): liczba RÓŻNYCH dokumentów (niezależnych orzeczeń) — to jest
        // sygnał „norma rządząca", odporny na jedno orzeczenie z długą listą przepisów procesowych.
        var votes = rows
            .SelectMany(r => JudgmentCitationParser.Parse(r.Text)
                .Where(c => c.Alias is not null)
                .Select(c => (c.Alias, c.Article, r.DocumentId)))
            .GroupBy(x => (x.Alias, x.Article))
            .Select(g => new { g.Key.Alias, g.Key.Article, Docs = g.Select(x => x.DocumentId).Distinct().Count(), Total = g.Count() })
            .OrderByDescending(x => x.Docs).ThenByDescending(x => x.Total)
            .ToList();

        var unattributed = rows.Sum(r => JudgmentCitationParser.Parse(r.Text).Count(c => c.Alias is null));
        Console.WriteLine($"   Chunków orzeczeń: {rows.Count}; cytowań z aktem: {votes.Sum(v => v.Total)}; bez aktu obok (odrzucone): {unattributed}");

        foreach (var v in votes.Take(12))
        {
            var exists = await ArticleExistsAsync(db, v.Alias!, v.Article, ct);
            var marker = v is { Alias: "KC", Article: "415" } ? "  ◄◄◄ norma właściwa" : "";
            Console.WriteLine($"   {v.Alias} art. {v.Article,-6} — {v.Docs} dok. / {v.Total} wyst. — w korpusie: {(exists ? "JEST" : "BRAK")}{marker}");
        }
        Console.WriteLine("   → Jeśli KC art. 415 ma ≥2 dokumenty i JEST w korpusie — most cytowań dostarczy normę bez ML.");
    }

    // ────────────────────────────── zapytania pomocnicze ──────────────────────────────

    /// <summary>Dense przez surowe SQL z rzutem halfvec — ta sama ścieżka indeksowa co
    /// <c>HybridRetriever.DenseAsync</c>; sonda musi mierzyć TEN SAM mechanizm, który działa w produkcie.</summary>
    private static async Task<List<ProbeHit>> DenseAsync(PrawoRagDbContext db, Vector qvec, int k, bool actsOnly, CancellationToken ct)
    {
        var filter = actsOnly ? " AND d.\"DocType\" = 'act'" : "";
        var sql = $$"""
            SELECT c."Id" AS "Id", (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024)) AS "Dist", d."DocType" AS "DocType"
            FROM chunks c
            JOIN documents d ON d."Id" = c."DocumentId"
            WHERE c."Embedding" IS NOT NULL{{filter}}
            ORDER BY "Dist"
            LIMIT {1}
            """;
        return await db.Database.SqlQueryRaw<ProbeHit>(sql, qvec, k).ToListAsync(ct);
    }

    private sealed record ProbeHit(Guid Id, double Dist, string DocType);

    private sealed record ChunkDetail(string Label, string? Article);

    private static async Task<Dictionary<Guid, ChunkDetail>> DetailsAsync(PrawoRagDbContext db, List<Guid> ids, CancellationToken ct) =>
        await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.ArticleNo, c.Document!.Title })
            .ToDictionaryAsync(x => x.Id, x => new ChunkDetail(x.Title, x.ArticleNo), ct);

    /// <summary>Rozpoznanie aktu po skrócie — ta sama heurystyka co <c>HybridRetriever.ResolveActAsync</c>
    /// (alias → najkrótszy pasujący tytuł), żeby sonda i produkt wskazywały ten sam dokument.</summary>
    private static async Task<string?> ResolveActAsync(PrawoRagDbContext db, string alias, CancellationToken ct)
    {
        var canonical = ActAliases.Canonical(alias);
        if (canonical is null) return null;
        return await db.Documents.AsNoTracking()
            .Where(d => d.DocType == "act" && EF.Functions.ILike(d.Title, "%" + canonical + "%"))
            .OrderBy(d => d.Title.Length)
            .Select(d => d.ExternalId)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<bool> ArticleExistsAsync(PrawoRagDbContext db, string alias, string article, CancellationToken ct)
    {
        var extId = await ResolveActAsync(db, alias, ct);
        return extId is not null && await db.Chunks.AsNoTracking()
            .AnyAsync(c => c.ArticleNo == article && c.Document!.ExternalId == extId, ct);
    }

    private static string Trim(string s, int max)
    {
        var flat = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
