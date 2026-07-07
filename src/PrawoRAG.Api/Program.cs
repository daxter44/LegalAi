using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Embeddings;
using PrawoRAG.Llm;
using PrawoRAG.Llm.Grounding;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPrawoRagStorage(builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("Brak ConnectionStrings:Db."));
builder.Services.AddTeiEmbeddings(builder.Configuration);
builder.Services.AddPrawoRagLlm(builder.Configuration); // claude | local (Ollama/llama.cpp) wg Llm:Provider
builder.Services.AddTeiReranker(builder.Configuration);  // IReranker tylko gdy Reranker:Enabled=true
builder.Services.AddScoped<IRetriever, HybridRetriever>();
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
builder.Services.AddOpenApi();

// Blazor Server (UI demo) w tym samym hoście — te same serwisy przez DI, bez skoku HTTP.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddHostedService<RetentionService>(); // retencja logów 6 mies. (C9/FE-4.4)
if (builder.Environment.IsDevelopment())
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors(); // pozwala odpalić tools/chat-tester.html jako plik lokalny (inne origin)
}

app.UseStaticFiles();
app.UseAntiforgery();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- Retrieval (debug / panel źródeł E4) ---
app.MapPost("/api/search", async (SearchRequest req, IRetriever retriever, IOptions<RetrievalOptions> opt, CancellationToken ct) =>
{
    var result = await retriever.RetrieveAsync(ToQuery(req.Query, req.Filters, req.TopK ?? opt.Value.TopK, opt.Value), ct);
    return Results.Ok(new
    {
        maxSimilarity = result.MaxSimilarity,
        wouldAbstain = AbstentionPolicy.ShouldAbstain(result, opt.Value.AbstentionThreshold),
        chunks = result.Chunks.Select(c => new { c.Text, c.Section, c.Source, c.Title, c.SourceUrl, c.Score, c.Similarity, locator = GroundedPrompt.LocatorLabel(c) }),
    });
});

// --- Chat z ugruntowaniem (SSE) ---
app.MapPost("/api/chat", async (HttpContext http, ChatRequest req, IRetriever retriever, ILlmProvider llm, IOptions<RetrievalOptions> opt, CancellationToken ct) =>
{
    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(string ev, object data)
    {
        await http.Response.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        var o = opt.Value;
        var result = await retriever.RetrieveAsync(ToQuery(req.Question, req.Filters, o.TopK, o), ct);

        // BRAMKA ABSTYNENCJI — rdzeń wartości: brak pokrycia → nie generujemy.
        if (AbstentionPolicy.ShouldAbstain(result, o.AbstentionThreshold))
        {
            await Send("abstain", new { message = AbstentionPolicy.Message, maxSimilarity = result.MaxSimilarity });
            await Send("done", new { abstained = true });
            return;
        }

        var (request, sources) = GroundedPrompt.Build(req.Question, result.Chunks);
        await Send("sources", sources);

        var full = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            full.Append(delta);
            await Send("token", new { text = delta });
        }

        // ANTY-FABRYKACJA — czy cytaty istnieją w dostarczonym kontekście.
        var contextTexts = result.Chunks.Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count);
        await Send("done", new { abstained = false, model = llm.ModelId, citationCheck = check });
    }
    catch (Exception ex)
    {
        await Send("error", new { message = ex.Message });
    }
});

app.MapRazorComponents<PrawoRAG.Api.Components.App>().AddInteractiveServerRenderMode();

app.Run();

static RetrievalQuery ToQuery(string text, FiltersDto? f, int topK, RetrievalOptions o) => new()
{
    Text = text,
    TopK = topK,
    CandidatesPerPath = o.CandidatesPerPath,
    MinChunkTokens = o.MinChunkTokens,
    CourtType = f?.CourtType,
    DateFrom = f?.DateFrom,
    DateTo = f?.DateTo,
    OnlyInForce = f?.OnlyInForce ?? false,
};

internal sealed record FiltersDto(string? CourtType, DateOnly? DateFrom, DateOnly? DateTo, bool OnlyInForce = false);
internal sealed record SearchRequest(string Query, FiltersDto? Filters, int? TopK);
internal sealed record ChatRequest(string Question, FiltersDto? Filters);

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 8;
    public int CandidatesPerPath { get; set; } = 50;
    public double AbstentionThreshold { get; set; } = AbstentionPolicy.DefaultThreshold;

    /// <summary>Minimalna liczba tokenów chunka w retrievalu (odsiew zdegenerowanych mini-chunków).</summary>
    public int MinChunkTokens { get; set; } = 20;
}
