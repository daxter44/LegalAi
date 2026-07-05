using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Embeddings;
using PrawoRAG.Eval;
using PrawoRAG.Llm;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Retrieval;

// Harness ewaluacyjny (E5): puszcza golden set przez retrieval (+ opcjonalnie czat) i liczy metryki.
// Domyślnie retrieval-only (tanie, bez LLM). Czat (anty-halucynacja): Eval:Chat=true lub arg --chat.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPrawoRagStorage(builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("Brak ConnectionStrings:Db."));
builder.Services.AddTeiEmbeddings(builder.Configuration);
builder.Services.AddPrawoRagLlm(builder.Configuration);
builder.Services.AddScoped<IRetriever, HybridRetriever>();

using var host = builder.Build();
var cfg = host.Services.GetRequiredService<IConfiguration>();

var topK = cfg.GetValue<int?>("Retrieval:TopK") ?? 8;
var threshold = cfg.GetValue<double?>("Retrieval:AbstentionThreshold") ?? 0.55;
var minChunkTokens = cfg.GetValue<int?>("Retrieval:MinChunkTokens") ?? 20;
var chat = (cfg.GetValue<bool?>("Eval:Chat") ?? false) || args.Contains("--chat");
var path = cfg["Eval:GoldenSetPath"] ?? Path.Combine(AppContext.BaseDirectory, "golden-set.json");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
var items = JsonSerializer.Deserialize<List<GoldenItem>>(await File.ReadAllTextAsync(path), json) ?? [];
Console.WriteLine($"Golden set: {items.Count} pozycji ({path}). Czat: {chat}. Próg: {threshold:F2}, TopK: {topK}.\n");

var verdicts = new List<ItemVerdict>();
var calib = new List<(bool ShouldAbstain, double MaxSim)>();

foreach (var item in items)
{
    using var scope = host.Services.CreateScope();
    var retriever = scope.ServiceProvider.GetRequiredService<IRetriever>();
    var res = await retriever.RetrieveAsync(
        new RetrievalQuery { Text = item.Question, TopK = topK, MinChunkTokens = minChunkTokens }, default);

    var wouldAbstain = AbstentionPolicy.ShouldAbstain(res, threshold);
    var retrieved = res.Chunks
        .Select(c => new RetrievedLocator(c.Locator?.EliId, c.Locator?.Article, c.Locator?.CaseNumber))
        .ToList();

    bool? citationsClean = null, abstained = null;
    if (chat && !item.NeedsLawyer)
    {
        if (wouldAbstain) abstained = true;
        else
        {
            var llm = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
            var (req, sources) = GroundedPrompt.Build(item.Question, res.Chunks);
            var sb = new StringBuilder();
            await foreach (var d in llm.StreamCompletionAsync(req, default)) sb.Append(d);
            var ctx = res.Chunks.Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
            citationsClean = CitationValidator.Validate(sb.ToString(), ctx, sources.Count).IsClean;
            abstained = false;
        }
    }

    var obs = new ItemObservation
    {
        Id = item.Id, MaxSimilarity = res.MaxSimilarity, WouldAbstain = wouldAbstain,
        Retrieved = retrieved, CitationsClean = citationsClean, Abstained = abstained,
    };
    var verdict = EvalScorer.Score(item, obs);
    verdicts.Add(verdict);
    calib.Add((item.ShouldAbstain, res.MaxSimilarity));

    Console.WriteLine($"[{item.Category,-11}] {item.Id,-16} sim={res.MaxSimilarity:F3} abstain={wouldAbstain,-5} " +
                      $"hit={(verdict.RetrievalHit?.ToString() ?? "—"),-5} halu_ok={(verdict.NoHallucination?.ToString() ?? "—")}");
}

Console.WriteLine();
Console.WriteLine(EvalScorer.Aggregate(verdicts, threshold).Format());
var best = EvalScorer.BestThreshold(calib);
Console.WriteLine($"Kalibracja progu: najlepszy ≈ {best.Threshold:F2} (trafność abstynencji {best.Accuracy:P0} na golden secie).");
Console.WriteLine("Jeśli najlepsza trafność jest niska, rozkłady similarity w korpusie i poza korpusem nakładają się → potrzebny reranker (5.4).");
