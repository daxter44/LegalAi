using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Storage;

namespace PrawoRAG.Eval;

/// <summary>
/// Eval odmów (`--refusals`): metryka nadrzędna fazy jakości pełnego korpusu. Bierze REALNE pytania
/// użytkowników z tabeli messages (z historią rozmowy — follow-upy odtwarzane wiernie), przepuszcza
/// przez AKTUALNY pipeline czatu (podwójny retrieval z marginesem, augmenter, OrderForGrounding,
/// GroundedPrompt, LLM) i raportuje per pytanie: BYŁO→JEST (odmowa progu / odmowa treściowa / OK)
/// + skład źródeł (akty/orzeczenia/nowele). Każda zmiana retrievalu/promptu dostaje odtąd liczbę,
/// nie wrażenie.
///
/// Zastrzeżenie: pytania zadane Z ZAŁĄCZNIKIEM odtwarzają się BEZ niego (fragmenty dokumentu nie są
/// persystowane — decyzja prywatności DOC #1); ich wynik interpretować ostrożnie.
/// Konfiguracja: Eval:RefusalsLimit (0 = wszystkie), Eval:RefusalsGenerate (false = tylko retrieval,
/// szybka diagnostyka składu źródeł bez kosztu LLM ~1 min/pytanie na Gemmie).
/// </summary>
public static class RefusalEvalRunner
{
    private sealed record ReplayItem(
        string Question, IReadOnlyList<ChatTurn> History, string Baseline, DateTimeOffset AskedAt);

    private sealed record ReplayResult(
        string Question, string Baseline, string Outcome, double Signal, bool GatePassed,
        int Acts, int Judgments, int Amendments, IReadOnlyList<string> TopSources, bool? CitationsClean);

    public static async Task RunAsync(IServiceProvider services, IConfiguration cfg, CancellationToken ct)
    {
        var limit = cfg.GetValue<int?>("Eval:RefusalsLimit") ?? 0;
        var generate = cfg.GetValue<bool?>("Eval:RefusalsGenerate") ?? true;
        var topK = cfg.GetValue<int?>("Retrieval:TopK") ?? 8;
        var threshold = cfg.GetValue<double?>("Retrieval:AbstentionThreshold") ?? 0.55;
        var minChunkTokens = cfg.GetValue<int?>("Retrieval:MinChunkTokens") ?? 20;
        var margin = cfg.GetValue<double?>("Retrieval:FollowUpSignalMargin") ?? FollowUpQuery.DefaultSignalMargin;

        var items = await LoadReplayItemsAsync(services, limit, ct);
        if (items.Count == 0)
        {
            Console.WriteLine("Brak pytań użytkowników w tabeli messages — nie ma czego odtwarzać.");
            return;
        }
        Console.WriteLine($"Eval odmów: {items.Count} realnych pytań (generacja: {generate}, próg: {threshold:F2}, TopK: {topK}).\n");

        Directory.CreateDirectory("logs");
        var reportPath = Path.Combine("logs", $"refusals-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        await using var report = new StreamWriter(reportPath) { AutoFlush = true };

        var results = new List<ReplayResult>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            using var scope = services.CreateScope();
            var r = await ReplayAsync(scope.ServiceProvider, item, generate, topK, threshold, minChunkTokens, margin, ct);
            results.Add(r);

            Console.WriteLine($"[{i + 1,3}/{items.Count}] BYŁO={r.Baseline,-16} JEST={r.Outcome,-16} " +
                              $"sim={r.Signal:F3} akty={r.Acts} orzecz={r.Judgments} now={r.Amendments} | {Trim(r.Question, 70)}");
            await report.WriteLineAsync(JsonSerializer.Serialize(r));
        }

        PrintSummary(results, generate);
        Console.WriteLine($"\nSurowe wyniki: {reportPath}");
    }

    /// <summary>Realne pytania z bazy: pary user→assistant per rozmowa (kolejność CreatedAt), historia
    /// wcześniejszych tur odtwarzana jak w UI (odpowiedź=null przy odmowie — nie kontynuujemy po odmowie).
    /// Dedup po znormalizowanym tekście (powtórki „jeszcze raz" liczą się raz — najnowsze wystąpienie).</summary>
    private static async Task<List<ReplayItem>> LoadReplayItemsAsync(IServiceProvider services, int limit, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();

        var messages = await db.Messages.AsNoTracking()
            .OrderBy(m => m.ConversationId).ThenBy(m => m.CreatedAt)
            .Select(m => new { m.ConversationId, m.Role, m.Content, m.CreatedAt, m.Abstained })
            .ToListAsync(ct);

        var items = new List<ReplayItem>();
        foreach (var convo in messages.GroupBy(m => m.ConversationId))
        {
            var history = new List<ChatTurn>();
            string? pendingQuestion = null;
            DateTimeOffset pendingAt = default;
            foreach (var m in convo)
            {
                if (m.Role == "user") { pendingQuestion = m.Content; pendingAt = m.CreatedAt; continue; }
                if (pendingQuestion is null) continue;

                var baseline = Classify(m.Abstained, m.Content);
                items.Add(new ReplayItem(pendingQuestion, history.ToList(), baseline, pendingAt));
                history.Add(new ChatTurn(pendingQuestion, baseline == "OK" ? m.Content : null));
                pendingQuestion = null;
            }
        }

        var deduped = items
            .GroupBy(i => Normalize(i.Question))
            .Select(g => g.OrderByDescending(i => i.AskedAt).First())
            .OrderByDescending(i => i.AskedAt)
            .ToList();
        return limit > 0 ? deduped.Take(limit).ToList() : deduped;
    }

    /// <summary>Odtworzenie logiki ChatService (podwójny retrieval z marginesem → bramka → augmenter →
    /// OrderForGrounding → prompt → LLM) — bez zależności Eval→Api; rozjazd z ChatService = rozjazd metryki,
    /// więc zmiany tam muszą lądować i tu (parytet jak /api/chat ↔ UI).</summary>
    private static async Task<ReplayResult> ReplayAsync(
        IServiceProvider sp, ReplayItem item, bool generate,
        int topK, double threshold, int minChunkTokens, double margin, CancellationToken ct)
    {
        var retriever = sp.GetRequiredService<IRetriever>();
        RetrievalQuery Query(string text) => new() { Text = text, TopK = topK, MinChunkTokens = minChunkTokens };

        var query = Query(item.Question);
        var result = await retriever.RetrieveAsync(query, ct);
        if (item.History.Count > 0)
        {
            var ctxText = FollowUpQuery.Contextualize(item.History.Select(t => t.Question).ToList(), item.Question);
            var ctxQuery = Query(ctxText);
            var ctxResult = await retriever.RetrieveAsync(ctxQuery, ct);
            if (FollowUpQuery.PickContextual(result.MaxSimilarity, ctxResult.MaxSimilarity, margin))
                (query, result) = (ctxQuery, ctxResult);
        }

        if (AbstentionPolicy.ShouldAbstain(result, threshold))
            return new ReplayResult(item.Question, item.Baseline, "odmowa-progu", result.MaxSimilarity,
                GatePassed: false, 0, 0, 0, [], null);

        var chunks = result.Chunks;
        var augmenter = sp.GetRequiredService<ITemporalAugmenter>();
        try { chunks = await augmenter.AugmentAsync(query, result.Chunks, ct); } catch { /* best-effort */ }
        chunks = GroundedPrompt.OrderForGrounding(chunks);

        var acts = chunks.Count(c => c.DocType == DocTypes.Act && c.AmendmentEffectiveDate is null);
        var amendments = chunks.Count(c => c.AmendmentEffectiveDate is not null);
        var judgments = chunks.Count - acts - amendments;
        var topSources = chunks.Take(5).Select(GroundedPrompt.LocatorLabel).ToList();

        if (!generate)
            return new ReplayResult(item.Question, item.Baseline, "(bez generacji)", result.MaxSimilarity,
                GatePassed: true, acts, judgments, amendments, topSources, null);

        var llm = sp.GetRequiredService<ILlmProvider>();
        var (req, sources) = GroundedPrompt.Build(item.Question, chunks, item.History);
        var sb = new StringBuilder();
        string outcome;
        bool? citationsClean = null;
        try
        {
            await foreach (var d in llm.StreamCompletionAsync(req, ct)) sb.Append(d);
            var answer = sb.ToString();
            if (string.IsNullOrWhiteSpace(answer)) outcome = "pusta";
            else if (answer.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase)) outcome = "odmowa-treściowa";
            else
            {
                outcome = "OK";
                var ctx = chunks.Select((c, k) => $"[{k + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
                citationsClean = CitationValidator.Validate(answer, ctx, sources.Count).IsClean;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            outcome = $"błąd: {e.GetType().Name}"; // Case 3 próba 1 — błędy generacji też są wynikiem, nie crashem evalu
        }

        return new ReplayResult(item.Question, item.Baseline, outcome, result.MaxSimilarity,
            GatePassed: true, acts, judgments, amendments, topSources, citationsClean);
    }

    private static void PrintSummary(List<ReplayResult> results, bool generate)
    {
        Console.WriteLine("\n=== PODSUMOWANIE ===");
        var n = results.Count;
        var wasRefusal = results.Count(r => r.Baseline is "odmowa-progu" or "odmowa-treściowa" or "pusta");
        Console.WriteLine($"BYŁO:  odmowy {wasRefusal}/{n} ({Pct(wasRefusal, n)}) — [{ByKind(results.Select(r => r.Baseline))}]");

        if (generate)
        {
            var isRefusal = results.Count(r => r.Outcome is "odmowa-progu" or "odmowa-treściowa" or "pusta" || r.Outcome.StartsWith("błąd"));
            Console.WriteLine($"JEST:  odmowy {isRefusal}/{n} ({Pct(isRefusal, n)}) — [{ByKind(results.Select(r => r.Outcome))}]");

            var fixedCount = results.Count(r => IsRefusalKind(r.Baseline) && r.Outcome == "OK");
            var regressed = results.Count(r => r.Baseline == "OK" && IsRefusalKind(r.Outcome));
            var dirty = results.Count(r => r.CitationsClean == false);
            Console.WriteLine($"Przejścia: naprawione {fixedCount}, regresje {regressed}; odpowiedzi z nieczystymi cytatami: {dirty}.");
        }

        Console.WriteLine($"Skład źródeł (średnio, gdy bramka przeszła): " +
            $"akty {Avg(results, r => r.Acts):F1}, orzeczenia {Avg(results, r => r.Judgments):F1}, nowele {Avg(results, r => r.Amendments):F1}.");

        // Cel z planu pilotażu: 10-25% odmów. Powyżej = dziury/retrieval; podejrzanie nisko = ryzyko halucynacji.
        static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:F0}%";
        static double Avg(List<ReplayResult> rs, Func<ReplayResult, int> f)
            => rs.Where(r => r.GatePassed) is var g && g.Any() ? g.Average(f) : 0;
        static bool IsRefusalKind(string s) => s is "odmowa-progu" or "odmowa-treściowa" or "pusta" || s.StartsWith("błąd");
        static string ByKind(IEnumerable<string> outcomes) => string.Join(", ",
            outcomes.GroupBy(o => o).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}"));
    }

    private static string Classify(bool abstained, string content) =>
        abstained ? "odmowa-progu"
        : string.IsNullOrWhiteSpace(content) ? "pusta"
        : content.Contains(GroundedPrompt.RefusalMarker, StringComparison.OrdinalIgnoreCase) ? "odmowa-treściowa"
        : "OK";

    /// <summary>Normalizacja do dedupu: białe znaki + spacja przed interpunkcją traktowane jak jej brak
    /// (bez tego „interes?" i „interes ?" to różne klucze — realny false-negative znaleziony 2026-07-19,
    /// dwie kopie tego samego pytania B2B w evalu z różnym wynikiem).</summary>
    private static string Normalize(string s)
    {
        var flat = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return PunctuationSpaceRe.Replace(flat, "$1").ToLowerInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex PunctuationSpaceRe =
        new(@"\s+([?!.,;:])", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Trim(string s, int max)
    {
        var flat = Normalize(s);
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
