using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// Sonda `--probe-chunk` (JAK-3, Case 5 raportu odmów): „mam pytanie i wiem, który chunk POWINIEN
/// wygrać — gdzie on ginie?". Dla pytania + wskazanego chunka raportuje pozycję na każdym etapie:
///   A. dokładny skan cosine fp32 (bez indeksu — prawda obiektywna),
///   B. dokładny skan cosine fp16/halfvec (czy kwantyzacja przesuwa ranking),
///   C. wyszukiwanie indeksowe HNSW jak w produkcji (ef_search=400) — rozjazd B↔C = strata recall indeksu,
///   D. tor BM25 (pełne pytanie; + czy tsquery w ogóle matchuje chunk),
///   E. fuzja RRF + dedup (symulacja identyczna z retrieverem) i finalny TopK realnego retrievera.
/// Wskazanie chunka: Eval:ProbeEli + Eval:ProbeArticle (akt+artykuł) ALBO Eval:ProbeTextLike
/// (fragment tekstu, ILIKE). Pytanie: argumenty po fladze. Tylko odczyt.
/// Uwaga: skany dokładne to seq-scan po 7,4M wierszy — pojedynczy przebieg trwa minuty; to sonda,
/// nie ścieżka produkcyjna.
/// </summary>
public static class ChunkProbe
{
    private const int ProductionK = 50;   // CandidatesPerPath — parytet z HybridRetriever
    private const int ProbeWindow = 200;  // ile pozycji przeszukujemy, zanim uznamy „poza oknem"

    public static async Task RunAsync(IServiceProvider services, IConfiguration cfg, string[] args, CancellationToken ct)
    {
        var question = string.Join(' ', args.SkipWhile(a => a != "--probe-chunk").Skip(1).Where(a => !a.StartsWith("--")));
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine("Użycie: --probe-chunk \"pytanie...\" + wskazanie chunka przez " +
                              "Eval__ProbeEli i Eval__ProbeArticle albo Eval__ProbeTextLike.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30)); // skany dokładne — celowo wolne

        var targets = await ResolveTargetsAsync(db, cfg, ct);
        if (targets.Count == 0)
        {
            Console.WriteLine("Nie znaleziono wskazanego chunka (sprawdź Eval__ProbeEli/ProbeArticle/ProbeTextLike).");
            return;
        }

        Console.WriteLine($"=== SONDA CHUNKA — pytanie: {question}");
        Console.WriteLine($"Cele: {targets.Count} chunk(ów) (badam maks. 3).\n");
        var qvec = new Vector(await embedder.EmbedQueryAsync(question, ct));

        foreach (var t in targets.Take(3))
        {
            Console.WriteLine($"── CEL: {t.Title} | art. {t.ArticleNo ?? "?"} | {t.TokenCount} tok | Embedding: {(t.HasEmbedding ? "jest" : "BRAK!")}");
            Console.WriteLine($"   „{Preview(t.Text)}”");
            if (!t.HasEmbedding) { Console.WriteLine("   → bez embeddingu tor gęsty go NIE zobaczy (kandydat po sanityzacji? patrz JAK-1).\n"); continue; }

            // A/B: dokładne rangi (count dystansów mniejszych niż dystans celu + 1).
            var dist32 = await ScalarAsync(db, """
                SELECT (c."Embedding" <=> {0}) AS "Value" FROM chunks c WHERE c."Id" = {1}
                """, ct, qvec, t.Id);
            var rank32 = await ScalarAsync(db, """
                SELECT count(*) + 1 AS "Value" FROM chunks c
                WHERE c."Embedding" IS NOT NULL AND (c."Embedding" <=> {0}) < {1}
                """, ct, qvec, dist32);

            var dist16 = await ScalarAsync(db, """
                SELECT (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024)) AS "Value" FROM chunks c WHERE c."Id" = {1}
                """, ct, qvec, t.Id);
            var rank16 = await ScalarAsync(db, """
                SELECT count(*) + 1 AS "Value" FROM chunks c
                WHERE c."Embedding" IS NOT NULL AND (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024)) < {1}
                """, ct, qvec, dist16);

            Console.WriteLine($"   A. exact fp32:   pozycja #{rank32,-6:F0} (sim={1 - dist32:F4})");
            Console.WriteLine($"   B. exact fp16:   pozycja #{rank16,-6:F0} (sim={1 - dist16:F4})  " +
                              (Math.Abs(rank16 - rank32) > 5 ? "◄ kwantyzacja PRZESUWA ranking" : "(zgodne z fp32)"));

            // C: HNSW jak w produkcji.
            var hnswRank = await HnswRankAsync(db, qvec, t.Id, ct);
            Console.WriteLine($"   C. HNSW (ef=400): {(hnswRank is { } h ? $"pozycja #{h}" : $"NIEOBECNY w top-{ProbeWindow}")}" +
                              (hnswRank is null && rank16 <= ProductionK ? "  ◄◄◄ STRATA RECALL INDEKSU (B mówi, że powinien być)" : ""));

            // D: BM25.
            var (bm25Rank, tsMatches) = await Bm25RankAsync(db, question, t.Id, ct);
            Console.WriteLine($"   D. BM25:          {(bm25Rank is { } b ? $"pozycja #{b}" : $"nieobecny w top-{ProbeWindow}")}; " +
                              $"tsquery {(tsMatches ? "MATCHUJE chunk" : "NIE matchuje chunka (AND wszystkich słów pytania — patrz Case 4)")}");

            // E: fuzja RRF + dedup (symulacja jak w HybridRetriever) na produkcyjnych k.
            await FusionReportAsync(db, qvec, question, t, ct);
            Console.WriteLine();
        }

        Console.WriteLine("Interpretacja: A wysoko + C nieobecny → indeks (ef_search/halfvec). A nisko → chunk/embedding");
        Console.WriteLine("(rozmycie — kierunek re-chunking per ustęp). A/C ok + E poza TopK → fuzja/dedup/konkurenci.");

        if (cfg.GetValue<bool?>("Eval:ProbeDumpTop") ?? false)
            await DumpTopDenseAsync(db, qvec, ct);

        if (cfg.GetValue<bool?>("Eval:ProbeDumpFused") ?? false)
            await DumpFusedAsync(db, qvec, question, ct);
    }

    /// <summary>CIT-2: zamiast dumpu SAMEGO dense (<see cref="DumpTopDenseAsync"/>) wypisuje PEŁNY,
    /// zfuzowany ranking RRF (dense+sparse, te same stałe co retriever) z klasyfikacją per pozycja
    /// (<see cref="ChunkClassifier"/>) — instrument do zmierzenia, CO realnie zajmuje fused #1-N, zanim
    /// powstanie zgadywany fix (CIT-3/CIT-4). Rysuje linię odcięcia puli TopK*4 (przed dedupem).
    /// „act/nowela/wariant/uchylony/cienkie-wyliczenie/orzeczenie" to klasy z realnego dumpu
    /// PRZYPADEK-BUDOWLA-BUDYNEK-UPOL — tu policzone automatem, nie okiem.</summary>
    private static async Task DumpFusedAsync(PrawoRagDbContext db, Vector qvec, string question, CancellationToken ct)
    {
        const int dumpN = 60;      // obejmij region #1-59, o który pyta raport (dedup cutoff = 32)
        const int dedupCutoff = 32; // TopK*4 — pula cięta PRZED dedupem (parytet z HybridRetriever)

        List<Guid> dense;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.ef_search = 400", ct);
            dense = await db.Database.SqlQueryRaw<Guid>($$"""
                SELECT c."Id" AS "Value" FROM chunks c
                WHERE c."Embedding" IS NOT NULL
                ORDER BY (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024))
                LIMIT {{ProductionK}}
                """, qvec).ToListAsync(ct);
        }
        var sparse = await db.Chunks.AsNoTracking()
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .Select(c => c.Id).Take(ProductionK).ToListAsync(ct);

        var rrf = new Dictionary<Guid, double>();
        for (var i = 0; i < dense.Count; i++) rrf[dense[i]] = rrf.GetValueOrDefault(dense[i]) + 1.0 / (60 + i + 1);
        for (var i = 0; i < sparse.Count; i++) rrf[sparse[i]] = rrf.GetValueOrDefault(sparse[i]) + 1.0 / (60 + i + 1);
        var pool = rrf.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(dumpN).ToList();

        var rows = await db.Chunks.AsNoTracking().Include(c => c.Document)
            .Where(c => pool.Contains(c.Id))
            .Select(c => new { c.Id, c.Document!.Title, c.Document.DocType, c.Section, c.Text, c.TokenCount })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);

        Console.WriteLine($"\n=== FUSED RRF top-{pool.Count} (klasyfikacja automatem — CIT-2) ===");
        var classes = new List<ChunkClass>(pool.Count);
        for (var i = 0; i < pool.Count; i++)
        {
            if (i == dedupCutoff)
                Console.WriteLine($"  ── linia odcięcia puli do dedupu (TopK*4 = {dedupCutoff}) — niżej ODPADA przed dedupem/rerankiem ──");
            if (!byId.TryGetValue(pool[i], out var r)) continue;
            var cls = ChunkClassifier.Classify(new ChunkFacts(r.DocType, r.Title, r.Section, r.Text, r.TokenCount));
            classes.Add(cls);
            Console.WriteLine($"  #{i + 1,-3} {Tag(cls.Kind),-12} [{Trim(r.Section ?? "", 26),-26}] {Trim(r.Title, 40),-40} {Preview(r.Text)}");
        }

        Console.WriteLine("\n  Rozkład klas (etykieta priorytetowa):");
        foreach (var g in classes.GroupBy(c => c.Kind).OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {Tag(g.Key),-14} {g.Count(),3}/{classes.Count}");
        Console.WriteLine("  Flagi ortogonalne (nakładki — jak ręczna tabela w PRZYPADEK-BUDOWLA):");
        Console.WriteLine($"    orzeczenia    {classes.Count(c => !c.IsAct),3}   akty {classes.Count(c => c.IsAct),3}");
        Console.WriteLine($"    nowele        {classes.Count(c => c.IsAmendmentAct),3}   warianty {classes.Count(c => c.IsVariant),3}   uchylony/pominięty {classes.Count(c => c.IsRepealedOrOmitted),3}");
        Console.WriteLine($"    punkt-wylicz. {classes.Count(c => c.IsEnumerationPoint),3}   cienkie (≤{ChunkClassifier.ThinTokenThreshold} tok) {classes.Count(c => c.IsThin),3}   cienkie+wyliczenie {classes.Count(c => c.IsEnumerationPoint && c.IsThin),3}");
        Console.WriteLine("  Interpretacja: dużo wariant/nowela/uchylony powyżej linii odcięcia → naprawa strukturalna");
        Console.WriteLine("  (CIT-3 dedup wariantów). Dużo cienkich punktów aktu bazowego → CIT-4 (degeneracja).");
    }

    private static string Tag(ChunkKind k) => k switch
    {
        ChunkKind.Judgment => "orzeczenie",
        ChunkKind.RepealedOrOmitted => "uchylony",
        ChunkKind.AmendmentVariant => "wariant",
        ChunkKind.AmendmentAct => "nowela",
        ChunkKind.ThinEnumeration => "cienkie-wyl",
        ChunkKind.BaseAct => "akt-bazowy",
        _ => k.ToString(),
    };

    /// <summary>Diagnostyka „co faktycznie jest w top-N": zamiast zgadywać czy konkurenci są trafni,
    /// wypisuje ich rzeczywistą treść — czy to prawdziwe, tylko merytorycznie niewłaściwe akty
    /// (naprawa: rerank/precyzja), czy kolejna, jeszcze niezłapana klasa zanieczyszczenia (naprawa:
    /// JAK-0/1 rozszerzone). Poszerzanie okna kandydatów NIE jest odpowiedzią — kolejne zapytanie
    /// i tak je przebije (feedback właściciela, raport Case 5).</summary>
    private static async Task DumpTopDenseAsync(PrawoRagDbContext db, Vector qvec, CancellationToken ct)
    {
        const int dumpN = 40;
        List<Guid> ids;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.ef_search = 400", ct);
            ids = await db.Database.SqlQueryRaw<Guid>($$"""
                SELECT c."Id" AS "Value" FROM chunks c
                WHERE c."Embedding" IS NOT NULL
                ORDER BY (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024))
                LIMIT {{dumpN}}
                """, qvec).ToListAsync(ct);
        }

        var rows = await db.Chunks.AsNoTracking().Include(c => c.Document)
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Document!.Title, c.Document.DocType, c.Section, c.Text })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);

        Console.WriteLine($"\n=== TOP-{dumpN} DENSE (treść — oceń okiem, czy faktycznie bliżej niż cel) ===");
        for (var i = 0; i < ids.Count; i++)
        {
            if (!byId.TryGetValue(ids[i], out var r)) continue;
            Console.WriteLine($"  #{i + 1,-3} [{r.DocType,-8}] {Trim(r.Title, 55),-55} {r.Section ?? "",-14} {Preview(r.Text)}");
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record Target(Guid Id, string Title, string? ArticleNo, int TokenCount, string Text, bool HasEmbedding);

    private static async Task<List<Target>> ResolveTargetsAsync(PrawoRagDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var eli = cfg["Eval:ProbeEli"];
        var article = cfg["Eval:ProbeArticle"];
        var textLike = cfg["Eval:ProbeTextLike"];

        var q = db.Chunks.AsNoTracking().Include(c => c.Document).AsQueryable();
        if (!string.IsNullOrWhiteSpace(eli)) q = q.Where(c => c.Document!.ExternalId == eli);
        if (!string.IsNullOrWhiteSpace(article)) q = q.Where(c => c.ArticleNo == article);
        if (!string.IsNullOrWhiteSpace(textLike)) q = q.Where(c => EF.Functions.ILike(c.Text, "%" + textLike + "%"));
        if (string.IsNullOrWhiteSpace(eli) && string.IsNullOrWhiteSpace(article) && string.IsNullOrWhiteSpace(textLike))
            return [];

        return await q.OrderBy(c => c.ChunkIndex).Take(10)
            .Select(c => new Target(c.Id, c.Document!.Title, c.ArticleNo, c.TokenCount, c.Text, c.Embedding != null))
            .ToListAsync(ct);
    }

    private static async Task<double> ScalarAsync(PrawoRagDbContext db, string sql, CancellationToken ct, params object[] args)
        => await db.Database.SqlQueryRaw<double>(sql, args).SingleAsync(ct);

    private static async Task<int?> HnswRankAsync(PrawoRagDbContext db, Vector qvec, Guid targetId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.ef_search = 400", ct);
        var ids = await db.Database.SqlQueryRaw<Guid>($$"""
            SELECT c."Id" AS "Value" FROM chunks c
            WHERE c."Embedding" IS NOT NULL
            ORDER BY (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024))
            LIMIT {{ProbeWindow}}
            """, qvec).ToListAsync(ct);
        var idx = ids.IndexOf(targetId);
        return idx < 0 ? null : idx + 1;
    }

    private static async Task<(int? Rank, bool TsMatches)> Bm25RankAsync(
        PrawoRagDbContext db, string question, Guid targetId, CancellationToken ct)
    {
        var ranked = await db.Chunks.AsNoTracking()
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .Select(c => c.Id)
            .Take(ProbeWindow)
            .ToListAsync(ct);
        var idx = ranked.IndexOf(targetId);
        if (idx >= 0) return (idx + 1, true);

        var matches = await db.Chunks.AsNoTracking()
            .AnyAsync(c => c.Id == targetId &&
                c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)), ct);
        return (null, matches);
    }

    /// <summary>Symulacja fuzji RRF + dedupu — te same stałe i wzory co HybridRetriever (RrfK=60,
    /// pula TopK*4, dedup po znormalizowanym tekście). Rozjazd tej symulacji z realnym retrieverem
    /// = zmiana w retrieverze bez aktualizacji sondy.</summary>
    private static async Task FusionReportAsync(PrawoRagDbContext db, Vector qvec, string question, Target t, CancellationToken ct)
    {
        List<Guid> dense;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.ef_search = 400", ct);
            dense = await db.Database.SqlQueryRaw<Guid>($$"""
                SELECT c."Id" AS "Value" FROM chunks c
                WHERE c."Embedding" IS NOT NULL
                ORDER BY (c."Embedding"::halfvec(1024) <=> {0}::halfvec(1024))
                LIMIT {{ProductionK}}
                """, qvec).ToListAsync(ct);
        }
        var sparse = await db.Chunks.AsNoTracking()
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, question)))
            .Select(c => c.Id).Take(ProductionK).ToListAsync(ct);

        var rrf = new Dictionary<Guid, double>();
        for (var i = 0; i < dense.Count; i++) rrf[dense[i]] = rrf.GetValueOrDefault(dense[i]) + 1.0 / (60 + i + 1);
        for (var i = 0; i < sparse.Count; i++) rrf[sparse[i]] = rrf.GetValueOrDefault(sparse[i]) + 1.0 / (60 + i + 1);

        var pool = rrf.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var poolRank = pool.IndexOf(t.Id) + 1;
        Console.WriteLine($"   E. fuzja RRF:     dense@{ProductionK}: {(dense.Contains(t.Id) ? $"#{dense.IndexOf(t.Id) + 1}" : "brak")}, " +
                          $"sparse@{ProductionK}: {(sparse.Contains(t.Id) ? $"#{sparse.IndexOf(t.Id) + 1}" : "brak")}, " +
                          $"pula RRF: {(poolRank > 0 ? $"#{poolRank}/{pool.Count}" : "poza pulą")} " +
                          $"(pula do dedupu = TopK*4 = 32 → {(poolRank is > 0 and <= 32 ? "WCHODZI do kandydatów" : "ODPADA przed dedupem")})");

        if (poolRank is > 0 and <= 32)
        {
            // Dedup: czy identyczny znormalizowany tekst o wyższym RRF zjada slot celu.
            var poolIds = pool.Take(32).ToList();
            var texts = await db.Chunks.AsNoTracking().Where(c => poolIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Text }).ToListAsync(ct);
            var norm = Normalize(t.Text);
            var twin = texts.FirstOrDefault(x => x.Id != t.Id && Normalize(x.Text) == norm
                && pool.IndexOf(x.Id) < pool.IndexOf(t.Id));
            Console.WriteLine(twin is null
                ? "      dedup: cel zachowuje własny slot."
                : $"      dedup: IDENTYCZNY tekst wyżej w RRF ({twin.Id}) — cel skolapsowany do bliźniaka (to OK, treść wchodzi).");
        }
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 140 ? flat : flat[..140] + "…";
    }
}
