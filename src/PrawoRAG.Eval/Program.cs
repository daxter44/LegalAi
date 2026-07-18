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
//
// ContentRootPath jawnie na AppContext.BaseDirectory — `dotnet run --project src/PrawoRAG.Eval`
// z korzenia repo NIE ustawia CWD na katalog projektu (zmierzone: ContentRootPath = CWD wywołania),
// więc appsettings.json bez tego jawnego zakotwiczenia po prostu się nie ładuje (cichy fallback do
// defaultów w kodzie — brak ConnectionStrings:Db, złe Embeddings:Dimensions). golden-set.json już
// stosował tę samą obronę (Path.Combine(AppContext.BaseDirectory, ...)) — teraz cały config też.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddPrawoRagStorage(builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("Brak ConnectionStrings:Db."));
builder.Services.AddTeiEmbeddings(builder.Configuration);
builder.Services.AddPrawoRagLlm(builder.Configuration);
builder.Services.AddTeiReranker(builder.Configuration);  // IReranker tylko gdy Reranker:Enabled=true
builder.Services.AddScoped<IRetriever, HybridRetriever>();
builder.Services.AddScoped<ITemporalAugmenter, TemporalAugmenter>(); // AKT-2: dokłada świeże nowele (parytet z /api/chat)

using var host = builder.Build();
var cfg = host.Services.GetRequiredService<IConfiguration>();

// Tryb egzaminacyjny (446 pytań ABC z egzaminów wstępnych 2025): --exam lub Eval:Exam=true.
// Osobny od golden-setu: mierzy wiedzę+retrieval (solo/rag/oracle), nie zachowania produktu.
if (args.Contains("--exam") || (cfg.GetValue<bool?>("Eval:Exam") ?? false))
{
    await ExamRunner.RunAsync(host.Services, cfg, default);
    return;
}

// Sonda act-lane (diagnoza „statut nieretrievalny", sesja 2026-07-17): --probe-akty [własne pytanie].
// Tylko odczyt bazy + TEI; bez LLM. Wynik decyduje o projekcie naprawy retrievalu aktów.
if (args.Contains("--probe-akty"))
{
    await ActLaneProbe.RunAsync(host.Services, args, default);
    return;
}

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
    var query = new RetrievalQuery { Text = item.Question, TopK = topK, MinChunkTokens = minChunkTokens };
    var res = await retriever.RetrieveAsync(query, default);

    // AKT-2/4b/6: augmentacja temporalna (parytet z /api/chat) — oznacza źródła-nowele i dokłada nowe
    // fragmenty; zwraca CAŁĄ zastępczą listę (caller PODMIENIA, nie dokleja). Best-effort: awaria nie
    // psuje ewaluacji. Score „Freshness" liczy się z augmentowanych źródeł.
    var augmenter = scope.ServiceProvider.GetRequiredService<ITemporalAugmenter>();
    var chunks = res.Chunks;
    try { chunks = await augmenter.AugmentAsync(query, res.Chunks, default); } catch { /* best-effort */ }

    // Abstynencja liczona z SUROWEGO retrievalu (augmentacja tylko dokłada, nie zmienia bramki progu).
    var wouldAbstain = AbstentionPolicy.ShouldAbstain(res, threshold);
    var retrieved = chunks
        .Select(c => new RetrievedLocator(c.Locator?.EliId, c.Locator?.Article, c.Locator?.CaseNumber))
        .ToList();

    bool? citationsClean = null, abstained = null;
    if (chat && !item.NeedsLawyer)
    {
        if (wouldAbstain) abstained = true; // bramka retrievalu odmówiła — LLM nie wołany
        else
        {
            var llm = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
            chunks = GroundedPrompt.OrderForGrounding(chunks); // parytet z /api/chat i ChatService
            var (req, sources) = GroundedPrompt.Build(item.Question, chunks);
            var sb = new StringBuilder();
            await foreach (var d in llm.StreamCompletionAsync(req, default)) sb.Append(d);
            var answer = sb.ToString();

            // Realna bramka: czy LLM SAM odmówił (fraza z GroundedPrompt), gdy źródła nie odpowiadają.
            if (answer.Contains("Nie mam wystarczających źródeł", StringComparison.OrdinalIgnoreCase))
            {
                abstained = true;
            }
            else
            {
                // Kontekst walidatora z TYCH SAMYCH chunków co prompt (augmentowane + uporządkowane) —
                // wcześniej szedł z res.Chunks: cytat źródła DOŁOŻONEGO przez augmenter wypadał poza
                // zakres, a numeracja [n] mogła wskazywać inny chunk niż w prompcie (parytet z /api/chat).
                var ctx = chunks.Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
                citationsClean = CitationValidator.Validate(answer, ctx, sources.Count).IsClean;
                abstained = false;
            }
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

// --- Diagnoza rozkładu similarity: czy liczby się różnią i czy „mamy odpowiedź" leży WYŻEJ niż „nie mamy" ---
var byId = items.ToDictionary(i => i.Id);
Console.WriteLine("\n=== SIMILARITY per pytanie (malejąco) ===");
foreach (var v in verdicts.OrderByDescending(v => v.MaxSimilarity))
{
    var oczek = byId[v.Id].ShouldAbstain ? "ODMOWA" : "ODPOWIEDŹ";
    Console.WriteLine($"  {v.MaxSimilarity:F4}  [{v.Category,-14}] oczek={oczek,-9} {v.Id}");
}

var distinct = verdicts.Select(v => Math.Round(v.MaxSimilarity, 4)).Distinct().Count();
Console.WriteLine($"\nRóżnych wartości similarity: {distinct} / {verdicts.Count}  (gdyby 1 → BUG: score nie zależy od pytania)");

Console.WriteLine("\n=== Średnia similarity per kategoria ===");
foreach (var g in verdicts.GroupBy(v => v.Category).OrderByDescending(g => g.Average(v => v.MaxSimilarity)))
    Console.WriteLine($"  {g.Key,-14}: śr={g.Average(v => v.MaxSimilarity):F4}  min={g.Min(v => v.MaxSimilarity):F4}  max={g.Max(v => v.MaxSimilarity):F4}  (n={g.Count()})");

// Kluczowy test: czy istnieje próg rozdzielający „mamy odpowiedź" od „nie mamy".
var mam = verdicts.Where(v => byId[v.Id] is { ShouldAbstain: false, NeedsLawyer: false }).Select(v => v.MaxSimilarity).ToList();
var nieMam = verdicts.Where(v => byId[v.Id].ShouldAbstain).Select(v => v.MaxSimilarity).ToList();
if (mam.Count > 0 && nieMam.Count > 0)
{
    Console.WriteLine($"\n„Mamy odpowiedź\" : śr={mam.Average():F4}  najniższy={mam.Min():F4}");
    Console.WriteLine($"„Nie mamy\"       : śr={nieMam.Average():F4}  najwyższy={nieMam.Max():F4}");
    Console.WriteLine(mam.Min() > nieMam.Max()
        ? $"→ ROZDZIELONE: każde „mamy\" ({mam.Min():F4}) jest wyżej niż każde „nie mamy\" ({nieMam.Max():F4}) — PRÓG ZADZIAŁA."
        : $"→ NAKŁADAJĄ SIĘ: najniższe „mamy\" ({mam.Min():F4}) ≤ najwyższe „nie mamy\" ({nieMam.Max():F4}) — brak czystego progu, bramka na etapie LLM (--chat).");
}
