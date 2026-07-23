using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
builder.Services.Configure<DiagnosticsOptions>(builder.Configuration.GetSection("Diagnostics"));
builder.Services.Configure<DocumentsOptions>(builder.Configuration.GetSection(DocumentsOptions.SectionName));

// --- Analiza dokumentów (spike SPK) — map-reduce per jednostka; Analysis:Enabled=false (domyślnie)
// chowa stronę /analiza. Store i runner to singletony: sesja żyje w pamięci procesu (id = bilet
// powrotu po F5), runner działa w tle poza obwodem Blazora.
builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection(AnalysisOptions.SectionName));
builder.Services.AddSingleton<AnalysisSessionStore>();
builder.Services.AddSingleton<IAnalysisStore, AnalysisStore>(); // raport BEZ treści dokumentu (AN-3)
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddOpenApi();

// Blazor Server (UI demo) w tym samym hoście — te same serwisy przez DI, bez skoku HTTP.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IConversationStore, ConversationStore>();
builder.Services.AddHostedService<RetentionService>(); // retencja logów 6 mies. (C9/FE-4.4)

// --- Bramka dostępu na zamknięty test (3.7) — kody zaproszeń + twarde dzienne limity kosztów ---
// Access:Enabled=false (domyślnie) = zachowanie jak dotąd; włączana dopiero w deployu.
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection(AccessOptions.SectionName));
var access = builder.Configuration.GetSection(AccessOptions.SectionName).Get<AccessOptions>() ?? new AccessOptions();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CostGuard>(); // twardy dzienny cap kosztów LLM (obok RateGuard — inna oś)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/wejscie";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Cookie.Name = "praworag.auth";
        // API (JSON/SSE) nie chcemy przekierowywać na HTML — 401 zamiast 302:
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- Bramka dostępu (3.7): strona wejścia (statyczny HTML, bez Blazora — omija pułapki render-mode
// przy SignInAsync) + wylogowanie. Zawsze dostępne bez auth. ---
static string WejscieHtml(string? error) => $$"""
    <!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>PrawoRAG — wejście</title>
    <style>body{font-family:system-ui,sans-serif;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0;background:#f5f5f4}
    .card{background:#fff;padding:2rem 2.5rem;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,.08);max-width:22rem}
    h1{font-size:1.2rem;margin:0 0 .5rem}p{color:#555;font-size:.9rem}input{width:100%;padding:.6rem;margin:.75rem 0;border:1px solid #ccc;border-radius:8px;box-sizing:border-box}
    button{width:100%;padding:.6rem;border:0;border-radius:8px;background:#1d4ed8;color:#fff;font-size:1rem;cursor:pointer}
    .err{color:#b91c1c;font-size:.85rem}</style></head><body>
    <form class="card" method="post" action="/wejscie">
      <h1>PrawoRAG — zamknięty test</h1>
      <p>Podaj kod zaproszenia otrzymany od zespołu.</p>
      {{(error is null ? "" : $"<p class=\"err\">{error}</p>")}}
      <input name="code" type="password" placeholder="kod zaproszenia" autofocus required>
      <button type="submit">Wejdź</button>
    </form></body></html>
    """;

// Publiczny landing na „/" (anonimowy — poza RequireAuthorization; statyczny HTML jak /wejscie).
// Zalogowany gość → prosto do aplikacji. Chat przeniesiony na /czat.
const string LandingHtml = """
    <!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>PrawoRAG — suwerenny research prawny</title>
    <style>
    :root{--accent:#1d4ed8}
    *{box-sizing:border-box}body{font-family:system-ui,sans-serif;margin:0;color:#1c1917;background:#f5f5f4;line-height:1.6}
    .wrap{max-width:52rem;margin:0 auto;padding:2rem 1.25rem}
    header{display:flex;align-items:center;gap:.5rem;font-weight:600}
    .logo{color:var(--accent);font-size:1.4rem}.tag{font-size:.75rem;color:#78716c;border:1px solid #d6d3d1;border-radius:6px;padding:.05rem .4rem}
    h1{font-size:1.9rem;margin:2.5rem 0 .5rem}.lead{font-size:1.15rem;color:#44403c;margin:0 0 2rem}
    .pillars{display:grid;gap:1rem;grid-template-columns:1fr}@media(min-width:640px){.pillars{grid-template-columns:1fr 1fr 1fr}}
    .card{background:#fff;border:1px solid #e7e5e4;border-radius:12px;padding:1.1rem}
    .card h2{font-size:1rem;margin:0 0 .35rem}.card p{font-size:.9rem;color:#57534e;margin:0}
    .cta{display:inline-block;margin:2rem 0 .5rem;padding:.7rem 1.4rem;background:var(--accent);color:#fff;text-decoration:none;border-radius:8px;font-weight:600}
    .muted{font-size:.85rem;color:#78716c}.muted a{color:var(--accent)}
    .note{margin-top:2.5rem;padding-top:1.25rem;border-top:1px solid #e7e5e4;font-size:.85rem;color:#78716c}
    </style></head><body><div class="wrap">
    <header><span class="logo">§</span> PrawoRAG <span class="tag">zamknięty test</span></header>

    <h1>Research prawny z prawdziwymi, klikalnymi cytatami.</h1>
    <p class="lead">Asystent dla polskich prawników oparty o orzecznictwo i akty prawne. Gdy brak źródła — mówi wprost, zamiast zmyślać. W 100% polski i europejski stos: Twoje pytania nie trafiają do amerykańskich chmur.</p>

    <div class="pillars">
      <div class="card"><h2>Cytaty, które można kliknąć</h2><p>Każda teza wskazuje konkretny przepis lub orzeczenie. Odpowiedź bez pokrycia w źródłach to uczciwa odmowa, nie konfabulacja.</p></div>
      <div class="card"><h2>Suwerenność danych</h2><p>Pytania, rozmowy i źródła nie opuszczają infrastruktury PL/UE. Argument dla tajemnicy zawodowej, którego nie daje research na amerykańskim API.</p></div>
      <div class="card"><h2>Świeżość prawa</h2><p>System oznacza nowelizacje jeszcze nie wchłonięte do tekstów jednolitych — pokazuje, od kiedy obowiązuje dana zmiana.</p></div>
    </div>

    <a class="cta" href="/wejscie">Mam kod zaproszenia → Wejdź</a>
    <p class="muted">Chcesz dołączyć do zamkniętego testu? Napisz do zespołu — liczba miejsc ograniczona pojemnością.</p>

    <p class="note">To wstępny research prawny do weryfikacji, nie porada. Zawsze sprawdzaj przy źródle. Więcej: <a href="/o-systemie">co system umie, a czego nie</a>.</p>
    </div></body></html>
    """;

app.MapGet("/", (HttpContext http) =>
    http.User.Identity?.IsAuthenticated == true
        ? Results.Redirect("/czat")
        : Results.Content(LandingHtml, "text/html; charset=utf-8"));

app.MapGet("/wejscie", () => Results.Content(WejscieHtml(null), "text/html; charset=utf-8"));

// Login-CSRF przy kodzie zaproszenia = ryzyko pomijalne (statyczny formularz bez tokenu) → DisableAntiforgery.
app.MapPost("/wejscie", async (HttpContext http, IOptions<AccessOptions> acc) =>
{
    var code = http.Request.Form["code"].ToString();
    if (!acc.Value.TryResolveInvite(code, out var tester))
        return Results.Content(WejscieHtml("Nieprawidłowy kod zaproszenia."), "text/html; charset=utf-8");

    var claims = new List<Claim> { new(ClaimTypes.Name, tester), new(ClaimTypes.Email, tester) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect("/czat");
}).DisableAntiforgery();

app.MapGet("/wyjscie", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/wejscie");
});

// Autoryzacja API: cookie ALBO nagłówek X-Invite-Code (wygoda curl/runbooków). Zwraca tożsamość testera
// (nazwę) do limitów, albo null = odmowa. Gdy bramka wyłączona — placeholder jak dotąd.
string? ResolveApiUser(HttpContext http)
{
    if (!access.Enabled) return http.User?.Identity?.Name ?? "demo@local";
    if (http.User?.Identity?.IsAuthenticated == true) return http.User.Identity.Name;
    return access.TryResolveInvite(http.Request.Headers["X-Invite-Code"], out var tester) ? tester : null;
}

// --- Retrieval (debug / panel źródeł E4) ---
app.MapPost("/api/search", async (HttpContext http, SearchRequest req, IRetriever retriever, IOptions<RetrievalOptions> opt, CancellationToken ct) =>
{
    if (ResolveApiUser(http) is null) return Results.Unauthorized(); // bramka 3.7 (cookie lub X-Invite-Code)
    var result = await retriever.RetrieveAsync(ToQuery(req.Query, req.Filters, req.TopK ?? opt.Value.TopK, opt.Value), ct);
    return Results.Ok(new
    {
        maxSimilarity = result.MaxSimilarity,
        wouldAbstain = AbstentionPolicy.ShouldAbstain(result, opt.Value.AbstentionThreshold),
        chunks = result.Chunks.Select(c => new { c.Text, c.Section, c.Source, c.Title, c.SourceUrl, c.Score, c.Similarity, locator = GroundedPrompt.LocatorLabel(c) }),
    });
}).RequireRateLimiting("api");

// --- Chat z ugruntowaniem (SSE) ---
app.MapPost("/api/chat", async (HttpContext http, ChatRequest req, IRetriever retriever, ITemporalAugmenter augmenter, ILlmProvider llm, IOptions<RetrievalOptions> opt, IOptions<DiagnosticsOptions> diag, CostGuard costGuard, CancellationToken ct) =>
{
    // Bramka 3.7: tożsamość (cookie lub X-Invite-Code) PRZED otwarciem streamu — 401 zamiast SSE.
    if (ResolveApiUser(http) is not { } apiUser) return Results.Unauthorized();

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(string ev, object data)
    {
        await http.Response.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        // Twardy dzienny cap kosztów LLM (obok rate-limitera HTTP) — parytet z UI/Chat.razor.
        if (!costGuard.TryAcquire(apiUser, out var limitReason))
        {
            await Send("error", new { message = CostGuard.LimitMessage(limitReason) });
            await Send("done", new { abstained = true });
            return Results.Empty;
        }

        var o = opt.Value;
        var history = (req.History ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Question))
            .Select(t => new ChatTurn(t.Question, t.Answer, t.SourceAnchors))
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
            return Results.Empty;
        }

        // AKT-2/4b: oznacz źródła-nowele (niezależnie jak trafiły do wyników) + dołóż nowe fragmenty
        // dotyczące pytanych artykułów (best-effort — parytet z UI/ChatService). Dostaje EFEKTYWNE
        // zapytanie (może być sklejone z historią) — to ono niesie cytaty z poprzednich tur.
        var chunks = result.Chunks;
        try { chunks = await augmenter.AugmentAsync(q, result.Chunks, ct); } catch { /* best-effort */ }

        // Norma przed narracjami (parytet z ChatService) — jeden porządek dla promptu/źródeł/walidatora.
        chunks = GroundedPrompt.OrderForGrounding(chunks);

        // Do promptu idzie ORYGINALNE pytanie + historia (nie sklejony tekst retrievalu).
        var (request, sources) = GroundedPrompt.Build(req.Question, chunks, history);
        await Send("sources", sources);

        // Tokeny in/out (parytet z UI): zbierane zawsze, w evencie done tylko przy włączonej fladze.
        LlmUsage? usage = null;
        request = request with { OnUsage = u => usage = u };

        var full = new StringBuilder();
        await foreach (var delta in llm.StreamCompletionAsync(request, ct))
        {
            full.Append(delta);
            await Send("token", new { text = delta });
        }

        // ANTY-FABRYKACJA — czy cytaty istnieją w dostarczonym kontekście.
        var contextTexts = chunks.Select((c, i) => $"[{i + 1}] {GroundedPrompt.LocatorLabel(c)}\n{c.Text}").ToList();
        var check = CitationValidator.Validate(full.ToString(), contextTexts, sources.Count);
        await Send("done", diag.Value.ShowTokenUsage
            ? new { abstained = false, model = llm.ModelId, citationCheck = check, usage }
            : (object)new { abstained = false, model = llm.ModelId, citationCheck = check });

        costGuard.Record(apiUser, full.Length); // dolicz wyjście do dziennego budżetu znaków
        return Results.Empty;
    }
    catch (Exception ex)
    {
        await Send("error", new { message = ex.Message });
        return Results.Empty;
    }
}).RequireRateLimiting("api");

// Bramka 3.7 na UI: niezalogowany → 302 na /wejscie (LoginPath cookie handlera). Gdy wyłączona — jak dotąd.
var components = app.MapRazorComponents<PrawoRAG.Api.Components.App>().AddInteractiveServerRenderMode();
if (access.Enabled) components.RequireAuthorization();

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

/// <summary>Jedna zakończona tura rozmowy w żądaniu SSE (kontekst follow-upów). Answer=null przy abstynencji.
/// SourceAnchors (opcjonalne) = etykiety/tytuły źródeł tamtej tury; klient API może je przysłać dla lepszej
/// kontekstualizacji follow-upu, brak → łagodna degradacja do cytatów z tekstu odpowiedzi.</summary>
internal sealed record HistoryTurnDto(string Question, string? Answer, IReadOnlyList<string>? SourceAnchors = null);

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 8;
    public int CandidatesPerPath { get; set; } = 50;
    public double AbstentionThreshold { get; set; } = AbstentionPolicy.DefaultThreshold;

    /// <summary>Minimalna liczba tokenów chunka w retrievalu (odsiew zdegenerowanych mini-chunków).</summary>
    public int MinChunkTokens { get; set; } = 20;

    /// <summary>Ile fragmentów pobiera strona „Wyszukiwarka" (retrieval-only, bez LLM). Większe niż
    /// czatowe TopK=8, bo wyniki grupujemy po dokumencie — chcemy pokryć kilkanaście–kilkadziesiąt
    /// dokumentów. Strojenie bez redeployu.</summary>
    public int SearchTopK { get; set; } = 25;

    /// <summary>Margines sygnału przy follow-upach: surowe dopytanie musi pobić wariant kontekstowy
    /// o tyle, żeby wygrać (różnice rzędu 1e-6 to szum — patrz <see cref="FollowUpQuery"/>).</summary>
    public double FollowUpSignalMargin { get; set; } = FollowUpQuery.DefaultSignalMargin;
}

/// <summary>Przełączniki diagnostyczne (domyślnie wszystko wyłączone — zero śladu w UI/SSE).</summary>
public sealed class DiagnosticsOptions
{
    /// <summary>Pokazuj tokeny in/out przy każdej odpowiedzi (badge w UI + pole `usage` w SSE done).
    /// Włączenie: `dotnet run -- --Diagnostics:ShowTokenUsage=true` albo env Diagnostics__ShowTokenUsage.</summary>
    public bool ShowTokenUsage { get; set; }
}
