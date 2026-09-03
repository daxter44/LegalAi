using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Analysis;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Eval;

/// <summary>Wynik jednej jednostki golden setu po przejściu przez fazę map (AJ-1b).</summary>
public sealed record UnitEvalResult(
    string DocId, string Heading, ExpectedVerdict Expected, bool NeedsLawyer,
    UnitVerdict Verdict, string Outcome,
    bool? NormHit, double Signal, bool GatePassed, int SecondRound,
    IReadOnlyList<string> TopSources, bool? CitationsClean, double Seconds, string? Error);

/// <summary>
/// Eval analizy dokumentów (`--analysis`, AJ-1b): każdy dokument golden setu → <see cref="LegalUnitSplitter"/>
/// → per jednostka DOKŁADNIE ten prompt, który produkcja wysyła w fazie map
/// (<see cref="AnalysisPrompts.MapQuestion"/>) → retrieval jak czat (<see cref="GapClosingRetrieval"/>,
/// augmenter, <see cref="GroundedPrompt.OrderForGrounding"/>) → bramka odmowy → LLM →
/// <see cref="AnalysisPrompts.ParseVerdict"/>. Odtwarza fazę map <c>AnalysisRunner</c> bez
/// <c>ChatService</c> (Eval nie referencuje Api) — różnice wobec produkcji: brak <c>AnswerGate</c>
/// (regeneracja po brudnych cytatach) i brak routera intencji (analiza i tak wymusza retrieval).
/// Metryki: recall wbudowanych ryzyk, fałszywe RYZYKO, BRAK ŹRÓDEŁ na § z treścią prawną,
/// trafienie normy (niezależne od LLM), czas. Konfiguracja: <c>Eval:AnalysisGenerate=false</c>
/// (albo <c>--no-generate</c>) = tylko retrieval + trafienie normy; <c>Eval:AnalysisDocs=id1,id2</c>
/// = podzbiór dokumentów; <c>Eval:AnalysisSetPath</c> = inny plik.
/// </summary>
public static class AnalysisEvalRunner
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration cfg, string[] args, CancellationToken ct)
    {
        var generate = (cfg.GetValue<bool?>("Eval:AnalysisGenerate") ?? true) && !args.Contains("--no-generate");
        var oracleProfile = args.Contains("--oracle-profile") || (cfg.GetValue<bool?>("Eval:AnalysisOracleProfile") ?? false);
        var profileEnabled = (cfg.GetValue<bool?>("Eval:AnalysisProfile") ?? true) && !args.Contains("--no-profile");
        var topK = cfg.GetValue<int?>("Retrieval:TopK") ?? 8;
        var threshold = cfg.GetValue<double?>("Retrieval:AbstentionThreshold") ?? 0.55;
        var gapClosingThreshold = cfg.GetValue<double?>("Retrieval:GapClosingTriggerThreshold") ?? AbstentionPolicy.DefaultThreshold;
        var minChunkTokens = cfg.GetValue<int?>("Retrieval:MinChunkTokens") ?? 20;
        var margin = cfg.GetValue<double?>("Retrieval:FollowUpSignalMargin") ?? FollowUpQuery.DefaultSignalMargin;
        var rerankMargin = cfg.GetValue<double?>("Retrieval:RerankSignalMargin") ?? FollowUpQuery.DefaultRerankSignalMargin;
        var setPath = cfg["Eval:AnalysisSetPath"] ?? AnalysisGoldenDoc.DefaultPath();
        var only = (cfg["Eval:AnalysisDocs"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        var docs = await AnalysisGoldenDoc.LoadAsync(setPath, ct);
        if (only.Count > 0) docs = docs.Where(d => only.Contains(d.Id)).ToList();
        if (docs.Count == 0) { Console.WriteLine($"Brak dokumentów w {setPath}."); return; }

        var totalUnits = docs.Sum(d => d.Units.Count);
        Console.WriteLine($"Eval analizy: {docs.Count} dokumentów, {totalUnits} jednostek ({setPath}). Generacja: {generate}, próg: {threshold:F2}, TopK: {topK}.\n");

        Directory.CreateDirectory("logs");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine("logs", $"analysis-{stamp}.jsonl");
        await using var raw = new StreamWriter(rawPath) { AutoFlush = true };

        var results = new List<UnitEvalResult>();
        var total = Stopwatch.StartNew();
        foreach (var doc in docs)
        {
            var units = LegalUnitSplitter.Split(doc.Pages);
            if (units.Count != doc.Units.Count)
            {
                // Klucz pilnowany testem; tu tylko obrona przed uruchomieniem na rozjechanym zestawie.
                Console.WriteLine($"[{doc.Id}] POMINIĘTY: splitter dał {units.Count} jednostek, klucz ma {doc.Units.Count}.");
                continue;
            }
            // Profil dokumentu (AJ-3/4): z LLM jak produkcja (gdy generacja), albo wyrocznia z klucza
            // (--oracle-profile) — górna granica efektu kotwicy bez zależności od modelu profilującego.
            DocumentProfile? profile = null;
            string profileSource = "brak";
            if (oracleProfile && doc.OracleProfile is { } op) { profile = op.ToProfile(); profileSource = "wyrocznia"; }
            else if (generate && profileEnabled)
            {
                using var scope = services.CreateScope();
                profile = await ProfileAsync(scope.ServiceProvider, units, ct);
                profileSource = profile is null ? "LLM: odrzucony/pusty" : "LLM";
            }
            Console.WriteLine($"=== {doc.Id} ({doc.Kind}, {units.Count} jednostek{(doc.NeedsLawyer ? ", NeedsLawyer" : "")}) — „{doc.Prompt}” | profil: {profileSource}{(profile?.RetrievalAnchor is { } an ? $" [{an}]" : "")}");
            var docClock = Stopwatch.StartNew();
            for (var i = 0; i < units.Count; i++)
            {
                var key = doc.Units[i];
                using var scope = services.CreateScope();
                var r = await EvaluateUnitAsync(scope.ServiceProvider, doc, units[i], key, profile, generate,
                    topK, threshold, gapClosingThreshold, minChunkTokens, margin, rerankMargin, ct);
                results.Add(r);
                await raw.WriteLineAsync(JsonSerializer.Serialize(r));
                Console.WriteLine($"  {r.Heading,-8} oczek={r.Expected,-14} jest={r.Outcome,-14} norma={Mark(r.NormHit)} " +
                                  $"sim={r.Signal:F3} r2={r.SecondRound} {r.Seconds,5:F0}s {(r.Error is null ? "" : "BŁĄD: " + Trim(r.Error, 60))}");
            }
            Console.WriteLine($"  czas dokumentu: {docClock.Elapsed.TotalMinutes:F1} min\n");
        }

        var summary = AnalysisEvalScorer.Aggregate(results, generate);
        Console.WriteLine(summary.Format());
        Console.WriteLine($"Łączny czas: {total.Elapsed.TotalMinutes:F1} min. Surowe wyniki: {rawPath}");

        // Snapshot do porównań między biegami (baseline vs po zmianie) — obok surowych wyników.
        var snapPath = Path.Combine("logs", $"analysis-summary-{stamp}.json");
        await File.WriteAllTextAsync(snapPath, JsonSerializer.Serialize(new { Generate = generate, Summary = summary, Results = results },
            new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"Podsumowanie: {snapPath}");
    }

    /// <summary>Profil jak w produkcji (AnalysisRunner.ProfileAsync): ten sam prompt, parser i strażnik.</summary>
    private static async Task<DocumentProfile?> ProfileAsync(IServiceProvider sp, IReadOnlyList<DocUnit> units, CancellationToken ct)
    {
        try
        {
            var llm = sp.GetRequiredService<ILlmProvider>();
            var req = new LlmRequest
            {
                Messages =
                [
                    new(ChatRole.System, DocumentProfilePrompts.SystemPrompt),
                    new(ChatRole.User, DocumentProfilePrompts.UserInput(DocumentProfilePrompts.BuildSample(units))),
                ],
                Temperature = 0,
            };
            var sb = new StringBuilder();
            await foreach (var d in llm.StreamCompletionAsync(req, ct)) sb.Append(d);
            return DocumentProfilePrompts.ParseClean(sb.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.WriteLine($"  profil: BŁĄD {e.GetType().Name} — analiza bez profilu");
            return null;
        }
    }

    private static async Task<UnitEvalResult> EvaluateUnitAsync(
        IServiceProvider sp, AnalysisGoldenDoc doc, DocUnit unit, AnalysisGoldenUnit key, DocumentProfile? profile, bool generate,
        int topK, double threshold, double gapClosingThreshold, int minChunkTokens, double margin, double rerankMargin,
        CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var question = AnalysisPrompts.MapQuestion(doc.Prompt, unit, profile);
        // AJ-4b: zapytanie retrievalu ROZDZIELONE od promptu LLM (jak AnalysisRunner → ChatService
        // z retrievalQuery): kotwica + treść fragmentu, bez instrukcji formatu werdyktu.
        var retrievalQuery = AnalysisPrompts.RetrievalQuery(unit, profile);
        var needsLawyer = doc.NeedsLawyer || key.NeedsLawyer;

        try
        {
            var retriever = sp.GetRequiredService<IRetriever>();
            RetrievalQuery Query(string text) => new() { Text = text, TopK = topK, MinChunkTokens = minChunkTokens };

            // To samo wejście retrievalu co ChatService (parytet jak w RefusalEvalRunner).
            var retrieval = await GapClosingRetrieval.RetrieveAsync(
                retriever, Query, retrievalQuery, [], margin, rerankMargin, gapClosingThreshold,
                sp.GetService<IQueryReformulator>(), maxExtraRounds: 1, ct);
            var (query, result) = (retrieval.Query, retrieval.Result);
            var secondRound = retrieval.ExtraRound ? 1 : 0;

            var chunks = result.Chunks;
            var augmenter = sp.GetRequiredService<ITemporalAugmenter>();
            try { chunks = await augmenter.AugmentAsync(query, result.Chunks, ct); } catch { /* best-effort */ }
            chunks = GroundedPrompt.OrderForGrounding(chunks);

            var normHit = NormHit(key, chunks);
            var topSources = chunks.Take(5).Select(GroundedPrompt.LocatorLabel).ToList();

            if (AbstentionPolicy.ShouldAbstain(result, threshold))
                return new UnitEvalResult(doc.Id, unit.Heading, key.ExpectedVerdict, needsLawyer,
                    UnitVerdict.NoSources, "odmowa-progu", normHit, result.MaxSimilarity, false, secondRound,
                    topSources, null, clock.Elapsed.TotalSeconds, null);

            if (!generate)
                return new UnitEvalResult(doc.Id, unit.Heading, key.ExpectedVerdict, needsLawyer,
                    UnitVerdict.Unknown, "(bez generacji)", normHit, result.MaxSimilarity, true, secondRound,
                    topSources, null, clock.Elapsed.TotalSeconds, null);

            var llm = sp.GetRequiredService<ILlmProvider>();
            var (req, sources) = GroundedPrompt.Build(question, chunks);
            var sb = new StringBuilder();
            await foreach (var d in llm.StreamCompletionAsync(req, ct)) sb.Append(d);
            var answer = sb.ToString();

            var (verdict, text) = AnalysisPrompts.ParseVerdict(answer);
            bool? citationsClean = null;
            if (verdict is not UnitVerdict.NoSources && !string.IsNullOrWhiteSpace(text))
            {
                var ctx = chunks.Select((c, k) => $"[{k + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
                citationsClean = CitationValidator.Validate(text, ctx, sources.Count).IsClean;
            }
            var outcome = string.IsNullOrWhiteSpace(answer) ? "pusta" : AnalysisPrompts.Label(verdict);
            return new UnitEvalResult(doc.Id, unit.Heading, key.ExpectedVerdict, needsLawyer,
                verdict, outcome, normHit, result.MaxSimilarity, true, secondRound,
                topSources, citationsClean, clock.Elapsed.TotalSeconds, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Błąd transportu/LLM też jest wynikiem (jak w RefusalEvalRunner) — nie wywraca biegu.
            return new UnitEvalResult(doc.Id, unit.Heading, key.ExpectedVerdict, needsLawyer,
                UnitVerdict.Error, "BŁĄD", null, 0, false, 0, [], null, clock.Elapsed.TotalSeconds,
                $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Trafienie normy: oczekiwany akt (i artykuł, jeśli podany) wśród źródeł jednostki.
    /// Null = klucz nie definiuje normy dla tej jednostki. Metryka retrievalu — LLM nie ma na nią wpływu.</summary>
    private static bool? NormHit(AnalysisGoldenUnit key, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (key.ExpectedEli is null) return null;
        return chunks.Any(c =>
            string.Equals(c.Locator?.EliId, key.ExpectedEli, StringComparison.OrdinalIgnoreCase) &&
            (key.ExpectedArticle is null ||
             string.Equals(c.Locator?.Article, key.ExpectedArticle, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Mark(bool? b) => b switch { true => "TAK", false => "nie", null => " - " };
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Zagregowane metryki evalu analizy (AJ-1b). Jednostki <c>NeedsLawyer</c> liczą się tylko do
/// BRAK ŹRÓDEŁ i czasu; recall/fałszywe RYZYKO wyłącznie na jednostkach z obiektywnym kluczem.</summary>
public sealed record AnalysisEvalSummary(
    int Units, int Scored,
    int PlantedRisks, int PlantedCaught,
    int SafeUnits, int FalseRisks,
    int LegalUnits, int LegalNoSources,
    int NormKeyed, int NormHits,
    int Unknown, int Errors,
    double MedianSeconds, double TotalMinutes,
    int FalseSkips = 0)
{
    public double? Recall => PlantedRisks == 0 ? null : (double)PlantedCaught / PlantedRisks;
    public double? FalseRiskRate => SafeUnits == 0 ? null : (double)FalseRisks / SafeUnits;
    public double? LegalNoSourcesRate => LegalUnits == 0 ? null : (double)LegalNoSources / LegalUnits;
    public double? NormHitRate => NormKeyed == 0 ? null : (double)NormHits / NormKeyed;

    public string Format()
    {
        var sb = new StringBuilder("=== PODSUMOWANIE EVALU ANALIZY ===\n");
        sb.AppendLine($"jednostek: {Units} (scorowanych merytorycznie: {Scored}; n małe — różnice 1–2 jednostek to szum)");
        sb.AppendLine($"recall wbudowanych ryzyk:          {PlantedCaught}/{PlantedRisks}  {Pct(Recall)}");
        sb.AppendLine($"fałszywe RYZYKO (na § bez wady):   {FalseRisks}/{SafeUnits}  {Pct(FalseRiskRate)}");
        sb.AppendLine($"BRAK ŹRÓDEŁ na § z treścią prawną: {LegalNoSources}/{LegalUnits}  {Pct(LegalNoSourcesRate)}");
        sb.AppendLine($"trafienie normy w źródłach:        {NormHits}/{NormKeyed}  {Pct(NormHitRate)}   (retrieval, niezależne od LLM)");
        sb.AppendLine($"fałszywe pominięcie (§ z treścią → BEZ TREŚCI PRAWNEJ): {FalseSkips}");
        sb.AppendLine($"werdykt „?\": {Unknown}, BŁĄD: {Errors}");
        sb.AppendLine($"czas: mediana {MedianSeconds:F0} s/jednostka, łącznie {TotalMinutes:F1} min");
        return sb.ToString();
    }

    private static string Pct(double? v) => v is null ? "(brak danych)" : $"({v:P0})";
}

public static class AnalysisEvalScorer
{
    public static bool IsRisk(UnitVerdict v) => v.IsRisk();

    /// <summary>Fałszywe pominięcie (AJ-5/AJ-10): § z treścią prawną (klucz Ok/Risk) zaklasyfikowany
    /// jako BEZ TREŚCI PRAWNEJ — najgroźniejszy błąd nowego zestawu werdyktów (ryzyko przepuszczone
    /// bez oceny). Liczone osobno, bo BRAK PODSTAWY to inna kategoria (retrieval), nie klasyfikacja.</summary>
    public static int FalseSkips(IReadOnlyList<UnitEvalResult> results) =>
        results.Count(r => r.Expected is ExpectedVerdict.Ok or ExpectedVerdict.Risk && r.Verdict == UnitVerdict.NoLegalContent);

    public static AnalysisEvalSummary Aggregate(IReadOnlyList<UnitEvalResult> results, bool generate)
    {
        var scored = results.Where(r => !r.NeedsLawyer && generate).ToList();
        var planted = scored.Where(r => r.Expected == ExpectedVerdict.Risk).ToList();
        var safe = scored.Where(r => r.Expected is ExpectedVerdict.Ok or ExpectedVerdict.NoLegalContent).ToList();
        var legal = results.Where(r => generate && r.Expected is ExpectedVerdict.Ok or ExpectedVerdict.Risk).ToList();
        var keyed = results.Where(r => r.NormHit is not null).ToList();
        var secs = results.Select(r => r.Seconds).OrderBy(s => s).ToList();
        var median = secs.Count == 0 ? 0 : secs[secs.Count / 2];

        return new AnalysisEvalSummary(
            results.Count, scored.Count,
            planted.Count, planted.Count(r => IsRisk(r.Verdict)),
            safe.Count, safe.Count(r => IsRisk(r.Verdict)),
            legal.Count, legal.Count(r => r.Verdict == UnitVerdict.NoSources),
            keyed.Count, keyed.Count(r => r.NormHit == true),
            results.Count(r => r.Verdict == UnitVerdict.Unknown && generate),
            results.Count(r => r.Verdict == UnitVerdict.Error),
            median, secs.Sum() / 60.0,
            generate ? FalseSkips(results) : 0);
    }
}
