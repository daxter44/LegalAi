using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
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
builder.Services.AddScoped<ITemporalAugmenter, TemporalAugmenter>(); // AKT-2: dokłada świeże nowele
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
builder.Services.AddOpenApi();

// Blazor Server (UI demo) w tym samym hoście — te same serwisy przez DI, bez skoku HTTP.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddHostedService<RetentionService>(); // retencja logów 6 mies. (C9/FE-4.4)

// --- Hardening (FE-7) ---
builder.Services.AddSingleton<RateGuard>(); // limiter kosztu ścieżki interaktywnej (Blazor/SignalR)
// DataProtection: klucze trwałe (ustaw DataProtection:KeysPath na wolumen w deployu — inaczej po
// restarcie psują się ciasteczka/sesje). Bez ścieżki = klucze efemeryczne (tylko dev).
var dp = builder.Services.AddDataProtection().SetApplicationName("PrawoRAG");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
// Rate limiting HTTP dla /api/* (ścieżka interaktywna Blazora limitowana osobno przez RateGuard).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("api", opt => { opt.Window = TimeSpan.FromMinutes(1); opt.PermitLimit = 60; opt.QueueLimit = 0; });
});
if (builder.Environment.IsDevelopment())
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Nagłówki bezpieczeństwa (C4). CSP dostrojony pod Blazor Server: skrypt frameworka z 'self',
// websocket SignalR w connect-src, style inline (UI reconnect/scoped). TLS/HSTS — na reverse proxy (C11).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseRateLimiter();

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
}).RequireRateLimiting("api");

// --- Chat z ugruntowaniem (SSE) ---
app.MapPost("/api/chat", async (HttpContext http, ChatRequest req, IRetriever retriever, ITemporalAugmenter augmenter, ILlmProvider llm, IOptions<RetrievalOptions> opt, CancellationToken ct) =>
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
        var history = (req.History ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Question))
            .Select(t => new ChatTurn(t.Question, t.Answer))
            .ToList();

        // Follow-upy (parytet z UI/ChatService): retrieval 2x — samo pytanie vs pytanie + poprzednie
        // pytania użytkownika. Wybór ASYMETRYCZNY z marginesem (FollowUpQuery.PickContextual): różnice
        // sygnału bywają szumem rzędu 1e-6, a koszt fałszywego surowego (śmieciowe źródła) >> koszt
        // fałszywego kontekstowego. Sekwencyjnie (scoped DbContext nie jest thread-safe).
        var q = ToQuery(req.Question, req.Filters, o.TopK, o);
        var result = await retriever.RetrieveAsync(q, ct);
        if (history.Count > 0)
        {
            var ctxText = FollowUpQuery.Contextualize(history.Select(t => t.Question).ToList(), req.Question);
            var ctxQuery = ToQuery(ctxText, req.Filters, o.TopK, o);
            var ctxResult = await retriever.RetrieveAsync(ctxQuery, ct);
            if (FollowUpQuery.PickContextual(result.MaxSimilarity, ctxResult.MaxSimilarity, o.FollowUpSignalMargin))
                (q, result) = (ctxQuery, ctxResult);
        }

        // BRAMKA ABSTYNENCJI — rdzeń wartości: brak pokrycia → nie generujemy.
        if (AbstentionPolicy.ShouldAbstain(result, o.AbstentionThreshold))
        {
            await Send("abstain", new { message = AbstentionPolicy.Message, maxSimilarity = result.MaxSimilarity });
            await Send("done", new { abstained = true });
            return;
        }

        // AKT-2/4b: oznacz źródła-nowele (niezależnie jak trafiły do wyników) + dołóż nowe fragmenty
        // dotyczące pytanych artykułów (best-effort — parytet z UI/ChatService). Dostaje EFEKTYWNE
        // zapytanie (może być sklejone z historią) — to ono niesie cytaty z poprzednich tur.
        var chunks = result.Chunks;
        try { chunks = await augmenter.AugmentAsync(q, result.Chunks, ct); } catch { /* best-effort */ }

        // Do promptu idzie ORYGINALNE pytanie + historia (nie sklejony tekst retrievalu).
        var (request, sources) = GroundedPrompt.Build(req.Question, chunks, history);
        await Send("sources", sources);

        var full = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            full.Append(delta);
            await Send("token", new { text = delta });
        }

        // ANTY-FABRYKACJA — czy cytaty istnieją w dostarczonym kontekście.
        var contextTexts = chunks.Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count);
        await Send("done", new { abstained = false, model = llm.ModelId, citationCheck = check });
    }
    catch (Exception ex)
    {
        await Send("error", new { message = ex.Message });
    }
}).RequireRateLimiting("api");

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
internal sealed record ChatRequest(string Question, FiltersDto? Filters, IReadOnlyList<HistoryTurnDto>? History = null);

/// <summary>Jedna zakończona tura rozmowy w żądaniu SSE (kontekst follow-upów). Answer=null przy abstynencji.</summary>
internal sealed record HistoryTurnDto(string Question, string? Answer);

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 8;
    public int CandidatesPerPath { get; set; } = 50;
    public double AbstentionThreshold { get; set; } = AbstentionPolicy.DefaultThreshold;

    /// <summary>Minimalna liczba tokenów chunka w retrievalu (odsiew zdegenerowanych mini-chunków).</summary>
    public int MinChunkTokens { get; set; } = 20;

    /// <summary>Margines sygnału przy follow-upach: surowe dopytanie musi pobić wariant kontekstowy
    /// o tyle, żeby wygrać (różnice rzędu 1e-6 to szum — patrz <see cref="FollowUpQuery"/>).</summary>
    public double FollowUpSignalMargin { get; set; } = FollowUpQuery.DefaultSignalMargin;
}
