using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// Eval egzaminacyjny (446 pytań ABC z egzaminów wstępnych 2025). Trzy tryby rozdzielające
/// odpowiedzialność za wynik:
///   solo   — sam LLM bez kontekstu (baza wiedzy modelu + sonda kontaminacji),
///   rag    — pełny retrieval (wynik systemu; delta rag−solo = wartość retrievalu),
///   oracle — pytanie + chunki DOKŁADNIE z artykułu wykazu (sufit: retrieval idealny;
///            delta oracle−rag = ile tracimy na retrievalu).
/// Dodatkowo per pytanie: czy artykuł z podstawy prawnej trafił do kontekstu i KTÓRY leg
/// go wniósł (structural/dense/lexical) — to domyka danymi dyskusję o polish/BM25.
/// </summary>
public static class ExamRunner
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration cfg, CancellationToken ct)
    {
        var path = cfg["Eval:ExamPath"] ?? Path.Combine(AppContext.BaseDirectory, "egzaminy-wstepne-2025.json");
        var modes = (cfg["Eval:ExamModes"] ?? "solo,rag,oracle").Split(',', StringSplitOptions.TrimEntries);
        var limit = cfg.GetValue<int?>("Eval:ExamLimit");
        var topK = cfg.GetValue<int?>("Retrieval:TopK") ?? 8;
        var threshold = cfg.GetValue<double?>("Retrieval:AbstentionThreshold") ?? 0.55;
        var minChunkTokens = cfg.GetValue<int?>("Retrieval:MinChunkTokens") ?? 20;

        var set = JsonSerializer.Deserialize<ExamSet>(await File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Nie wczytano zestawu: {path}");
        var items = limit is { } l ? set.Items.Take(l).ToList() : set.Items;
        Console.WriteLine($"Egzamin: {set.Name} ({items.Count} pytań, stan prawny {set.StanPrawnyNa}). Tryby: {string.Join('+', modes)}.\n");

        Directory.CreateDirectory("logs");
        var reportPath = Path.Combine("logs", $"exam-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        await using var report = new StreamWriter(reportPath) { AutoFlush = true };

        var results = new List<ExamItemResult>();
        long inTok = 0, outTok = 0;
        var actCache = new Dictionary<string, string?>(); // wskazówka aktu → ExternalId (null = nierozpoznany)

        for (var idx = 0; idx < items.Count; idx++)
        {
            var item = items[idx];
            var basis = ExamBasisParser.Parse(item.PodstawaPrawna);
            using var scope = services.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
            var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();

            foreach (var mode in modes)
            {
                ExamItemResult r;
                switch (mode)
                {
                    case "solo":
                        r = await AskAsync(llm, ExamPrompt.Solo(item), item, "solo", ct);
                        break;

                    case "rag":
                    {
                        var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
                        // Zapytanie = trzon + opcje: opcje niosą tekst właściwego przepisu (tor gęsty),
                        // trzon niesie ewentualny cytat „art. N" (tor strukturalny).
                        var query = new RetrievalQuery
                        {
                            Text = ExamPrompt.QuestionBlock(item),
                            TopK = topK, MinChunkTokens = minChunkTokens,
                        };
                        var res = await retriever.RetrieveAsync(query, ct);
                        var (hit, leg, resolved) = await BasisHitAsync(db, actCache, basis, res.Chunks, ct);
                        r = await AskAsync(llm, ExamPrompt.Rag(item, res.Chunks), item, "rag", ct) with
                        {
                            WouldAbstain = AbstentionPolicy.ShouldAbstain(res, threshold),
                            BasisHit = hit, BasisHitLeg = leg, BasisResolved = resolved,
                        };
                        break;
                    }

                    case "oracle":
                    {
                        var chunks = await OracleChunksAsync(db, actCache, basis, ct);
                        if (chunks.Count == 0) { r = new ExamItemResult(item.Nr, "oracle", null, false, BasisResolved: false); break; }
                        r = await AskAsync(llm, ExamPrompt.Rag(item, chunks), item, "oracle", ct) with { BasisResolved = true };
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"Nieznany tryb egzaminu '{mode}' (dozwolone: solo, rag, oracle).");
                }

                results.Add(r);
                await report.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    r.Nr, r.Mode, gold = item.Prawidlowa, model = r.ModelLetter?.ToString(),
                    r.Correct, r.WouldAbstain, r.BasisHit, r.BasisHitLeg, r.BasisResolved,
                    domena = basis.Domain, zrodlo = item.Zrodlo, r.RawAnswer,
                }));
            }

            if ((idx + 1) % 25 == 0)
                Console.WriteLine($"  … {idx + 1}/{items.Count} pytań (tokeny: ↓{inTok} ↑{outTok})");
        }

        Console.WriteLine();
        PrintReport(results, items);
        Console.WriteLine($"\nSzczegóły per pytanie: {reportPath}");
        Console.WriteLine($"Tokeny łącznie: wejście {inTok}, wyjście {outTok}.");

        async Task<ExamItemResult> AskAsync(ILlmProvider llm, LlmRequest req, ExamItem item, string mode, CancellationToken token)
        {
            req = req with { OnUsage = u => { inTok += u.InputTokens ?? 0; outTok += u.OutputTokens ?? 0; } };
            var sb = new StringBuilder();
            try
            {
                await foreach (var d in llm.StreamCompletionAsync(req, token)) sb.Append(d);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  ! LLM błąd przy #{item.Nr} ({mode}): {ex.GetBaseException().Message}");
                return new ExamItemResult(item.Nr, mode, null, false, RawAnswer: $"(błąd: {ex.GetBaseException().Message})");
            }
            var letter = ExamPrompt.ParseLetter(sb.ToString());
            return new ExamItemResult(item.Nr, mode, letter,
                Correct: letter is { } ch && item.Prawidlowa == ch.ToString(),
                RawAnswer: sb.ToString().Trim());
        }
    }

    /// <summary>Czy którykolwiek chunk kontekstu pochodzi z aktu+artykułu podstawy prawnej — i który leg go wniósł.</summary>
    private static async Task<(bool? Hit, string? Leg, bool Resolved)> BasisHitAsync(
        PrawoRagDbContext db, Dictionary<string, string?> cache, ExamBasisParser.Basis basis,
        IReadOnlyList<RetrievedChunk> chunks, CancellationToken ct)
    {
        var actId = await ResolveActAsync(db, cache, basis, ct);
        if (actId is null || basis.Article is null) return (null, null, false); // poza korpusem / nieparsowalna

        var hit = chunks.FirstOrDefault(c =>
            c.Locator?.EliId == actId && ExamBasisParser.ArticleEquals(c.Locator?.Article, basis.Article));
        if (hit is null) return (false, null, true);

        // Atrybucja lega bez zmian w retrieverze: structural ma Score=MaxValue, dense ma Similarity,
        // trafienie czysto leksykalne — Score z RRF bez Similarity.
        var leg = hit.Score == double.MaxValue ? "structural"
            : hit.Similarity is not null ? "dense"
            : "lexical";
        return (true, leg, true);
    }

    /// <summary>Chunki dokładnie z artykułu wykazu (tryb oracle) — jak retrieval strukturalny, ale z klucza odpowiedzi.</summary>
    private static async Task<List<RetrievedChunk>> OracleChunksAsync(
        PrawoRagDbContext db, Dictionary<string, string?> cache, ExamBasisParser.Basis basis, CancellationToken ct)
    {
        var actId = await ResolveActAsync(db, cache, basis, ct);
        if (actId is null || basis.Article is null) return [];

        // ArticleNo porównujemy po stronie klienta (normalizacja indeksów górnych) — lista artykułów aktu jest mała.
        var arts = await db.Chunks.AsNoTracking()
            .Where(c => c.Document!.ExternalId == actId && c.ArticleNo != null)
            .Select(c => c.ArticleNo!).Distinct().ToListAsync(ct);
        var match = arts.FirstOrDefault(a => ExamBasisParser.ArticleEquals(a, basis.Article));
        if (match is null) return [];

        var rows = await db.Chunks.AsNoTracking().Include(c => c.Document)
            .Where(c => c.Document!.ExternalId == actId && c.ArticleNo == match)
            .OrderBy(c => c.ChunkIndex).Take(8).ToListAsync(ct);
        return rows.Select(c => new RetrievedChunk
        {
            ChunkId = c.Id, DocumentId = c.DocumentId, Text = c.Text, Section = c.Section,
            Source = c.Document!.Source, DocType = c.Document.DocType, Title = c.Document.Title,
            SourceUrl = c.Document.SourceUrl, Score = 1,
        }).ToList();
    }

    /// <summary>Wskazówka aktu → ExternalId w korpusie: skrót kodeksu (alias → najkrótszy tytuł),
    /// „o <nazwie ustawy>" / Konstytucja → dopasowanie trigramowe tytułu. Cache per run.</summary>
    private static async Task<string?> ResolveActAsync(
        PrawoRagDbContext db, Dictionary<string, string?> cache, ExamBasisParser.Basis basis, CancellationToken ct)
    {
        var key = basis.ActAbbrev ?? basis.UstawaHint ?? (basis.Domain == "konstytucja" ? "konstytucja" : null);
        if (key is null) return null;
        if (cache.TryGetValue(key, out var cached)) return cached;

        string? id = null;
        if (ActAliases.Canonical(basis.ActAbbrev) is { } canonical)
        {
            id = await db.Documents.AsNoTracking()
                .Where(d => d.DocType == "act" && EF.Functions.ILike(d.Title, "%" + canonical + "%"))
                .OrderBy(d => d.Title.Length)
                .Select(d => d.ExternalId).FirstOrDefaultAsync(ct);
        }
        else
        {
            var hint = basis.Domain == "konstytucja" ? "Konstytucja Rzeczypospolitej Polskiej" : basis.UstawaHint!;
            var best = await db.Documents.AsNoTracking()
                .Where(d => d.DocType == "act")
                .Select(d => new { d.ExternalId, Sim = EF.Functions.TrigramsSimilarity(d.Title, hint) })
                .OrderByDescending(x => x.Sim).FirstOrDefaultAsync(ct);
            id = best is not null && best.Sim >= 0.15 ? best.ExternalId : null;
        }
        cache[key] = id;
        return id;
    }

    private static void PrintReport(List<ExamItemResult> results, List<ExamItem> items)
    {
        var byNr = items.ToDictionary(i => i.Nr);
        var modes = results.Select(r => r.Mode).Distinct().ToList();

        Console.WriteLine("=== WYNIKI (próg zdawalności ludzi: 66,7%; losowo: 33,3%) ===");
        foreach (var mode in modes)
        {
            var rs = results.Where(r => r.Mode == mode).ToList();
            var answered = rs.Where(r => r.ModelLetter is not null).ToList();
            var acc = rs.Count > 0 ? (double)rs.Count(r => r.Correct) / rs.Count : 0;
            var line = $"{mode,-7}: {acc:P1} ({rs.Count(r => r.Correct)}/{rs.Count})";
            if (answered.Count < rs.Count) line += $"  [bez odpowiedzi: {rs.Count - answered.Count}]";
            if (mode == "oracle")
            {
                var covered = rs.Count(r => r.BasisResolved == true);
                var accCov = covered > 0 ? (double)rs.Count(r => r is { Correct: true, BasisResolved: true }) / covered : 0;
                line += $"  [podstawa w korpusie: {covered}/{rs.Count}; trafność na pokrytych: {accCov:P1}]";
            }
            Console.WriteLine(line);

            // Rozkład liter modelu vs klucz — wykrywa skrzywienie („model zawsze mówi C").
            var dist = string.Join(' ', "ABC".Select(l => $"{l}={answered.Count(r => r.ModelLetter == l)}"));
            var gold = string.Join(' ', "ABC".Select(l => $"{l}={rs.Count(r => byNr[r.Nr].Prawidlowa == l.ToString())}"));
            Console.WriteLine($"         litery modelu: {dist}   klucz: {gold}");
        }

        var rag = results.Where(r => r.Mode == "rag").ToList();
        if (rag.Count > 0)
        {
            Console.WriteLine("\n=== RETRIEVAL (tryb rag) ===");
            var resolved = rag.Where(r => r.BasisResolved == true).ToList();
            Console.WriteLine($"Podstawa prawna rozpoznana w korpusie: {resolved.Count}/{rag.Count} " +
                              $"(reszta = akt poza korpusem lub nieparsowalny wykaz — mapa luk korpusu!)");
            if (resolved.Count > 0)
            {
                var hits = resolved.Where(r => r.BasisHit == true).ToList();
                Console.WriteLine($"Artykuł z wykazu W KONTEKŚCIE: {hits.Count}/{resolved.Count} ({(double)hits.Count / resolved.Count:P1})");
                foreach (var g in hits.GroupBy(r => r.BasisHitLeg).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  wniósł go leg {g.Key}: {g.Count()}");
                var accHit = hits.Count > 0 ? (double)hits.Count(r => r.Correct) / hits.Count : 0;
                var misses = resolved.Where(r => r.BasisHit == false).ToList();
                var accMiss = misses.Count > 0 ? (double)misses.Count(r => r.Correct) / misses.Count : 0;
                Console.WriteLine($"Trafność gdy artykuł w kontekście: {accHit:P1}; gdy go brak: {accMiss:P1} (różnica = cena chybień retrievalu)");
            }
            Console.WriteLine($"Bramka abstynencji (próg) zapaliłaby się przy: {rag.Count(r => r.WouldAbstain == true)}/{rag.Count}");
        }

        Console.WriteLine("\n=== Trafność per dziedzina (rag) ===");
        foreach (var g in rag.GroupBy(r => ExamBasisParser.Parse(byNr[r.Nr].PodstawaPrawna).Domain)
                     .OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-18}: {(double)g.Count(r => r.Correct) / g.Count():P0}  (n={g.Count()})");
    }
}
