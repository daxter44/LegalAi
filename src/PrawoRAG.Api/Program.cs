using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Stripe;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services;
using PrawoRAG.Api.Services.Auth;
using PrawoRAG.Api.Services.Billing;
using PrawoRAG.Api.Services.Legal;
using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Storage.Entities;
using PrawoRAG.Domain;
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
builder.Services.Configure<GroundingOptions>(builder.Configuration.GetSection("Grounding"));
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
builder.Services.AddScoped<IDocumentReader, DocumentReader>(); // widok pełnego dokumentu (/dokument/{id})
builder.Services.AddHostedService<RetentionService>(); // retencja logów 6 mies. (C9/FE-4.4)

// --- Bramka dostępu na zamknięty test (3.7) — kody zaproszeń + twarde dzienne limity kosztów ---
// Access:Enabled=false (domyślnie) = zachowanie jak dotąd; włączana dopiero w deployu.
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection(AccessOptions.SectionName));
var access = builder.Configuration.GetSection(AccessOptions.SectionName).Get<AccessOptions>() ?? new AccessOptions();
builder.Services.AddSingleton(TimeProvider.System);

// --- Plany i uprawnienia (E1, blok B) -----------------------------------------------------------
// Uprawnienie czytamy z NASZEJ bazy, nigdy od dostawcy płatności w ścieżce zapytania (E3 tylko
// zapisuje tu stan z webhooków). CostGuard trzyma dwie osie: limit planu na okres rozliczeniowy
// konta ORAZ globalne capy dobowe chroniące pojemność — patrz komentarz w klasie.
builder.Services.Configure<PlanOptions>(builder.Configuration.GetSection(PlanOptions.SectionName));
builder.Services.AddSingleton<IEntitlements, Entitlements>();
builder.Services.AddSingleton<IUsageCounters, PostgresUsageCounters>();
builder.Services.AddSingleton<CostGuard>();

// --- Konta użytkowników (E1, blok A) ------------------------------------------------------------
// Auth:Enabled=false (domyślnie) = świat sprzed kont: bramka invite albo otwarty dev, bit w bit.
// Auth:Enabled=true = rejestracja/logowanie kontem; bramka invite wtedy NIE jest mapowana.
// Dwie ścieżki wykluczają się świadomie: tożsamością invite jest nazwa testera, a ciasteczko Identity
// jest walidowane znacznikiem bezpieczeństwa konta — principal bez konta zostałby natychmiast wylogowany.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

// --- Płatności (E3/US-3.1 — spike) --------------------------------------------------------------
// Billing:Enabled=false (domyślnie) = trasy /platnosc/* nie istnieją. Wymaga kont: bez konta nie ma
// czego subskrybować ani komu przypisać planu.
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.SectionName));
// Analityka bez cookies (US-2.12, Umami self-hosted) — snippet konfigurowany RAZ, statycznie,
// bo część powłok HTML (auth, konto, strony prawne) to statyczne buildery bez DI.
var analyticsOptions = builder.Configuration.GetSection(AnalyticsOptions.SectionName).Get<AnalyticsOptions>() ?? new AnalyticsOptions();
AnalyticsSnippet.Configure(analyticsOptions);
var billingOptions = builder.Configuration.GetSection(BillingOptions.SectionName).Get<BillingOptions>() ?? new BillingOptions();
if (billingOptions.Enabled)
{
    if (!authOptions.Enabled)
        throw new InvalidOperationException("Billing:Enabled=true wymaga Auth:Enabled=true (plan przypisuje się do konta).");
    if (string.IsNullOrWhiteSpace(billingOptions.SecretKey) || string.IsNullOrWhiteSpace(billingOptions.WebhookSecret))
        throw new InvalidOperationException(
            "Billing:Enabled=true wymaga Billing:SecretKey i Billing:WebhookSecret. " +
            "Bez sekretu podpisu webhook nie odróżni zdarzenia Stripe od dowolnego POST-a z internetu.");

    StripeConfiguration.ApiKey = billingOptions.SecretKey;
}

static void ApiReturns401Instead302(CookieAuthenticationOptions o)
{
    // API (JSON/SSE) nie chcemy przekierowywać na HTML — 401 zamiast 302:
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
}

if (authOptions.Enabled)
{
    builder.Services.AddIdentityCore<AppUserEntity>(o =>
        {
            // Hasła: stawiamy na DŁUGOŚĆ, nie na wymuszone znaki specjalne — dłuższa fraza jest
            // trudniejsza do złamania niż „Haslo1!", a nie prowokuje zapisywania na karteczce.
            o.Password.RequiredLength = 10;
            o.Password.RequireDigit = false;
            o.Password.RequireUppercase = false;
            o.Password.RequireLowercase = false;
            o.Password.RequireNonAlphanumeric = false;
            o.Password.RequiredUniqueChars = 4;

            // Blokada po serii nieudanych prób — hamuje zgadywanie haseł (obok limitera HTTP „auth").
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.Lockout.AllowedForNewUsers = true;

            o.User.RequireUniqueEmail = true;
            // Bez potwierdzonego adresu nie ma logowania: inaczej limit darmowy byłby dostępny
            // na dowolny zmyślony adres, a reset hasła stałby się kanałem wysyłki spamu.
            o.SignIn.RequireConfirmedEmail = true;
        })
        .AddEntityFrameworkStores<PrawoRagDbContext>()
        .AddDefaultTokenProviders()
        .AddSignInManager()
        .AddClaimsPrincipalFactory<AppClaimsFactory>() // imię + e-mail w ciasteczku (nagłówek, powitanie)
        .AddErrorDescriber<PolishIdentityErrorDescriber>();

    // Odnośniki z e-maili (potwierdzenie, reset) są ważne krótko — zgubiona skrzynka nie zostaje
    // wieczną furtką do konta.
    builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(6));

    // Reset hasła zmienia znacznik bezpieczeństwa konta, ale ciasteczko jest z nim porównywane co
    // ten interwał (domyślnie 30 min). 5 minut = ukradziona sesja umiera szybko po resecie, a koszt
    // to jedno zapytanie o konto na obwód na 5 minut.
    builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));

    // Bezpieczniki startowe: te dwie pomyłki konfiguracyjne czynią konta niebezpiecznymi, więc
    // produkcja ma się NIE URUCHOMIĆ zamiast działać źle. (1) Bez PublicBaseUrl adres w e-mailu
    // buduje się z nagłówka Host sterowanego przez klienta — droga do listu resetującego hasło
    // z odnośnikiem na cudzy serwer. (2) Provider "log" wypisywałby tokeny do logu produkcyjnego.
    if (!builder.Environment.IsDevelopment())
    {
        if (string.IsNullOrWhiteSpace(authOptions.PublicBaseUrl))
            throw new InvalidOperationException(
                "Auth:Enabled=true poza dev wymaga Auth:PublicBaseUrl (odnośniki w e-mailach).");
        if (!string.Equals(builder.Configuration[$"{EmailOptions.SectionName}:Provider"], "resend",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Auth:Enabled=true poza dev wymaga Email:Provider=resend (log ujawniałby tokeny).");
    }

    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
    builder.Services.ConfigureApplicationCookie(o =>
    {
        o.LoginPath = "/logowanie";
        o.LogoutPath = "/wylogowanie";
        o.AccessDeniedPath = "/logowanie";
        // Nazwa parametru powrotu musi zgadzać się z tą, którą czyta strona logowania — inaczej
        // po zalogowaniu użytkownik ląduje na stronie startowej zamiast tam, gdzie chciał wejść.
        o.ReturnUrlParameter = "powrot";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Cookie.Name = "praworag.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax; // Lax, nie Strict: powrót z odnośnika w e-mailu ma działać
        // W produkcji ciasteczko tylko po HTTPS; w dev localhost bywa po http.
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        ApiReturns401Instead302(o);
    });

    // Poczta transakcyjna: Resend albo zapis do logu (dev). Wybór z konfiguracji, nie z #if.
    var emailProvider = builder.Configuration[$"{EmailOptions.SectionName}:Provider"] ?? "log";
    if (string.Equals(emailProvider, "resend", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddHttpClient<IAppEmailSender, ResendEmailSender>(c =>
        {
            c.BaseAddress = new Uri("https://api.resend.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
    else
        builder.Services.AddSingleton<IAppEmailSender, LogEmailSender>();
}
else
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/wejscie";
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.Cookie.Name = "praworag.auth";
            ApiReturns401Instead302(o);
        });
}
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
    // Ścieżki kont osobno i ciaśniej: to one są celem zgadywania haseł, wyliczania adresów
    // i zalewania cudzych skrzynek listami „zresetuj hasło". Klucz = adres klienta, nie globalnie,
    // żeby jeden bot nie zablokował logowania wszystkim pozostałym.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "nieznany",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 12, QueueLimit = 0 }));
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
    // form-action: Chrome sprawdza tę dyrektywę też po przekierowaniu (nie tylko cel formularza),
    // więc POST /platnosc/start -> 302 na Stripe Checkout wymaga jawnego dopuszczenia hosta Stripe.
    var formAction = billingOptions.Enabled
        ? "'self' https://checkout.stripe.com https://billing.stripe.com"
        : "'self'";
    // Analityka bez cookies (US-2.12, Umami self-hosted): w CSP dochodzi WYŁĄCZNIE origin naszej
    // instancji (skrypt + beacon /api/send) i tylko przy skonfigurowanym Analytics — bez
    // konfiguracji polityka zostaje bajt w bajt jak dotąd.
    var umami = AnalyticsSnippet.CspOrigin is { Length: > 0 } origin ? " " + origin : "";
    h["Content-Security-Policy"] =
        $"default-src 'self'; script-src 'self'{umami}; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        $"font-src 'self'; connect-src 'self' ws: wss:{umami}; frame-ancestors 'none'; base-uri 'self'; form-action {formAction}";
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
    <title>OmniaSI — wejście</title>
    <style>body{font-family:system-ui,sans-serif;display:flex;justify-content:center;align-items:center;min-height:100vh;margin:0;background:#f5f5f4}
    .card{background:#fff;padding:2rem 2.5rem;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,.08);max-width:22rem}
    h1{font-size:1.2rem;margin:0 0 .5rem}p{color:#555;font-size:.9rem}input{width:100%;padding:.6rem;margin:.75rem 0;border:1px solid #ccc;border-radius:8px;box-sizing:border-box}
    button{width:100%;padding:.6rem;border:0;border-radius:8px;background:#1d4ed8;color:#fff;font-size:1rem;cursor:pointer}
    .err{color:#b91c1c;font-size:.85rem}</style></head><body>
    <form class="card" method="post" action="/wejscie">
      <h1>OmniaSI — zamknięty test</h1>
      <p>Podaj kod zaproszenia otrzymany od zespołu.</p>
      {{(error is null ? "" : $"<p class=\"err\">{error}</p>")}}
      <input name="code" type="password" placeholder="kod zaproszenia" autofocus required>
      <button type="submit">Wejdź</button>
    </form></body></html>
    """;

// Publiczny landing na „/" (anonimowy — poza RequireAuthorization; statyczny HTML jak /wejscie).
// Zalogowany gość → prosto do aplikacji. Chat przeniesiony na /czat.
// UWAGA przed publikacja (2026-09-07) — DWA ROZNE warunki, nie jeden:
// (a) LOKALIZACJA MODELU: eyebrow w hero i kafel „Lokalizacja danych" twierdza, ze model jezykowy
//     dziala w UE. Prawda dopiero po przelaczeniu na CloudFerro/Sherlock (PLAN-DEPLOY-2026-09-02 §0.2);
//     do tego czasu mowimy o DANYCH w UE, nie o modelu.
// (b) BRAK TRENINGU: filar 04 i FAQ „Co sie dzieje z moimi pytaniami" obiecuja, ze zapytania nie
//     trenuja modeli. To gwarancja UMOWNA po stronie dostawcy, nie wlasciwosc naszego kodu —
//     audyt OWASP zostawil ja jako W4 (POZA ZAKRESEM, decyzja biznesowa wlasciciela). Nie publikowac,
//     zanim warunek zero-retention nie bedzie w umowie z dostawca modelu.
const string LandingHtml = """
    <!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>OmniaSI — research prawny na źródłach</title>
    <link rel="icon" type="image/svg+xml" href="/favicon.svg">
    <link rel="stylesheet" href="/css/tokens.css">
    <style>
    *{box-sizing:border-box}body{font-family:var(--sl-font-base);margin:0;color:var(--sl-on-dark);background:#0F1218;line-height:1.6}
    a{color:var(--sl-accent);text-decoration:none}
    html{scroll-behavior:smooth}
    /* Nawigacja przyklejona (2026-09-01): po skoku do #roznice/#cennik header ma zostac widoczny. */
    .nav{position:sticky;top:0;z-index:100;display:flex;align-items:center;gap:28px;padding:18px 6vw;background:rgb(23 27 36 / .85);backdrop-filter:blur(10px);border-bottom:1px solid rgb(199 208 236 / .12)}
    [id]{scroll-margin-top:76px} /* kotwice nie chowaja sie pod przyklejonym paskiem */
    .brand{display:flex;align-items:center;gap:7px;color:var(--sl-on-dark);font-family:var(--sl-font-base);font-size:24px;letter-spacing:-.01em}
    .brand .omnia{font-weight:700}
    .brand .si{font-weight:400;color:var(--sl-on-dark-accent)}
    .mark{width:36px;height:36px;display:block}
    .nav .links{margin-left:auto;display:flex;gap:24px;align-items:center;flex-wrap:wrap}
    .nav .links a{color:#9BA3B7;font-size:15px;font-weight:500}
    .btn{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:0 22px;border-radius:12px;font-size:15px;font-weight:700;color:#fff;background:var(--sl-gradient);box-shadow:var(--sl-shadow-accent)}
    .btn-line{display:inline-flex;align-items:center;min-height:44px;padding:0 20px;border-radius:12px;font-size:15px;font-weight:600;color:var(--sl-on-dark);border:1px solid rgb(199 208 236 / .3)}
    /* Chip konta dla zalogowanego (spójny z .app-who w aplikacji) */
    .navwho{display:inline-flex;align-items:center;gap:8px;text-decoration:none;color:#9BA3B7;font-size:14px;font-weight:600}
    a.navwho:hover{color:var(--sl-on-dark)}
    .navavatar{width:30px;height:30px;border-radius:9999px;background:rgb(199 208 236 / .15);color:var(--sl-on-dark-soft);display:inline-flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;flex-shrink:0}
    /* Hamburger (mobile) — <details> bez JS, jak .nav-burger w aplikacji */
    .nav-burger{display:none;position:relative;margin-left:auto}
    .nav-burger>summary{list-style:none;cursor:pointer;user-select:none;color:#9BA3B7;font-size:20px;line-height:1;padding:8px 12px;border-radius:8px}
    .nav-burger>summary::-webkit-details-marker{display:none}
    .nav-burger[open]>summary{color:var(--sl-on-dark);background:rgb(199 208 236 / .12)}
    .nav-sheet{position:absolute;right:0;top:calc(100% + 8px);z-index:200;min-width:230px;display:flex;flex-direction:column;gap:6px;padding:10px;background:#171B24;border:1px solid rgb(199 208 236 / .18);border-radius:12px;box-shadow:0 10px 20px -4px rgb(0 0 0 / .4)}
    .nav-sheet a{color:#9BA3B7;font-size:15px;font-weight:500;padding:8px 12px;border-radius:8px;white-space:nowrap}
    .nav-sheet a:hover{color:var(--sl-on-dark);background:rgb(199 208 236 / .1)}
    .nav-sheet .btn,.nav-sheet .btn-line{justify-content:center;color:#fff}
    @media(max-width:720px){
      .nav{gap:14px}
      .nav .links{display:none}
      .nav-burger{display:block}
    }
    .hero{position:relative;overflow:hidden;padding:90px 6vw 110px;text-align:center;background:linear-gradient(180deg,#0F1218 0%,#142450 70%,#16224A 100%)}
    .glow1,.glow2{position:absolute;border-radius:9999px;pointer-events:none}
    .glow1{left:-180px;top:60px;width:560px;height:560px;background:radial-gradient(circle,rgb(37 99 235 / .28) 0%,rgb(37 99 235 / 0) 70%)}
    .glow2{right:-140px;top:220px;width:620px;height:620px;background:radial-gradient(circle,rgb(124 58 237 / .24) 0%,rgb(124 58 237 / 0) 70%)}
    .eyebrow{position:relative;display:inline-flex;align-items:center;gap:10px;padding:6px 16px;border-radius:9999px;border:1px solid rgb(147 180 255 / .3);background:rgb(147 180 255 / .08);color:var(--sl-on-dark-accent);font-size:13.5px;font-weight:600}
    .eyebrow i{width:7px;height:7px;border-radius:9999px;background:var(--sl-on-dark-accent);box-shadow:0 0 10px var(--sl-on-dark-accent)}
    h1{position:relative;font-family:var(--sl-font-display);font-size:clamp(2.2rem,6vw,4.6rem);line-height:1.1;font-weight:700;letter-spacing:-.015em;margin:26px auto 0;max-width:1000px}
    .lead{position:relative;font-size:clamp(1rem,2vw,1.2rem);color:var(--sl-on-dark-soft);max-width:62ch;margin:24px auto 0}
    .heroctas{position:relative;display:flex;gap:14px;justify-content:center;flex-wrap:wrap;margin-top:28px}
    .quiet{position:relative;font-size:13px;color:var(--sl-on-dark-faint);margin-top:18px}
    .light{background:var(--sl-bg);color:var(--sl-text-primary);padding:90px 6vw}
    h2{font-family:var(--sl-font-display);font-size:clamp(1.6rem,3.4vw,2.5rem);font-weight:700;letter-spacing:-.01em;margin:0 0 10px}
    .sub{font-size:17px;color:var(--sl-text-secondary);max-width:72ch;margin:0 0 40px}
    .bento{display:grid;gap:20px;grid-template-columns:1fr}
    @media(min-width:960px){.bento{grid-template-columns:2fr 1fr}.span2{grid-column:span 1}}
    .cardw{background:var(--sl-surface);border-radius:16px;box-shadow:var(--sl-shadow-card);padding:30px;display:flex;flex-direction:column;gap:14px}
    .k{font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:var(--sl-accent)}
    .cardw h3{font-family:var(--sl-font-display);font-size:24px;line-height:1.25;font-weight:700;margin:0}
    .cardw p{font-size:15px;line-height:1.65;color:var(--sl-text-secondary);margin:0}
    .dark{background:var(--sl-hero-gradient);color:var(--sl-on-dark)}
    .dark p{color:var(--sl-on-dark-soft)}
    .cmp{display:grid;gap:16px;grid-template-columns:1fr}@media(min-width:760px){.cmp{grid-template-columns:1fr 1fr}}
    .ans{border-radius:12px;padding:18px;display:flex;flex-direction:column;gap:10px;font-size:13.5px;line-height:1.6}
    .ans .who{display:flex;align-items:center;gap:8px;font-weight:700;color:var(--sl-text-primary)}
    .tag{margin-left:auto;display:inline-flex;padding:2px 10px;border-radius:9999px;color:#fff;font-size:11px;font-weight:700;letter-spacing:.03em}
    .ans-bad{border:1px solid var(--sl-error-border);background:var(--sl-error-bg);color:var(--sl-text-secondary)}
    .ans-good{border:1.5px solid var(--sl-accent);background:var(--sl-accent-light);color:var(--sl-text-primary);box-shadow:0 4px 12px -2px rgb(37 99 235 / .15)}
    .q{padding:10px 16px;border-radius:12px;background:var(--sl-bg-secondary);font-size:14px;align-self:flex-start}
    .cite{display:inline-flex;padding:1px 7px;border-radius:9999px;background:#fff;color:var(--sl-accent);font-size:11.5px;font-weight:700}
    .foot-note{font-size:13px;color:var(--sl-text-tertiary)}
    /* Widoczny fokus klawiatury (a11y, leftover RED): akcent na jasnych sekcjach, jaśniejszy
       błękit na ciemnych (akcent tonie w granacie hero). */
    a:focus-visible{outline:3px solid var(--sl-accent);outline-offset:2px;border-radius:8px}
    .nav a:focus-visible,.hero a:focus-visible,.dark a:focus-visible,.table a:focus-visible,footer a:focus-visible{outline-color:var(--sl-on-dark-accent)}
    .table{background:#171B24;border-radius:16px;padding:36px 40px;color:#E7E9F0;margin-top:40px;overflow-x:auto}
    .table h3{font-family:var(--sl-font-display);font-size:26px;margin:0 0 18px}
    .trow{display:grid;grid-template-columns:minmax(0,1fr) 130px 150px;align-items:center;border-bottom:1px solid rgb(199 208 236 / .12);padding:13px 0;font-size:15px;color:var(--sl-on-dark-soft)}
    .trow:last-child{border-bottom:0}
    .trow.h{font-size:13px;font-weight:700;color:#6E7690}
    .trow .c{text-align:center;font-weight:700}
    .yes{color:var(--sl-success)}.no{color:#6E7690}
    .analys{display:grid;gap:48px;grid-template-columns:1fr;align-items:center}
    @media(min-width:960px){.analys{grid-template-columns:1fr 1fr}}
    .checks{display:flex;flex-direction:column;gap:12px;font-size:15px}
    .checks span::before{content:"\2713\0020";color:var(--sl-success);font-weight:700}
    .unit{border:1px solid var(--sl-border);border-radius:12px;padding:13px 15px;display:flex;flex-direction:column;gap:7px;background:var(--sl-surface);font-size:13px;color:var(--sl-text-secondary)}
    .u-pill{align-self:flex-start;display:inline-flex;padding:2px 9px;border-radius:9999px;font-size:11px;font-weight:700}
    .doc{background:var(--sl-surface);border-radius:16px;box-shadow:var(--sl-shadow-lg);padding:24px;display:flex;flex-direction:column;gap:10px}
    .price{display:grid;gap:24px;grid-template-columns:1fr;max-width:880px;margin:0 auto}
    @media(min-width:760px){.price{grid-template-columns:1fr 1fr}}
    .plan{background:var(--sl-surface);border:1px solid var(--sl-border);border-radius:16px;padding:32px;display:flex;flex-direction:column;gap:16px;color:var(--sl-text-primary)}
    .plan.pro{border:2px solid var(--sl-accent);box-shadow:var(--sl-shadow-lift);position:relative}
    .plan .badge-top{position:absolute;top:-14px;left:32px;padding:4px 14px;border-radius:9999px;background:var(--sl-gradient);color:#fff;font-size:12.5px;font-weight:700}
    .plan .name{font-size:14px;font-weight:700;letter-spacing:.05em;text-transform:uppercase;color:var(--sl-text-secondary)}
    .plan.pro .name{color:var(--sl-accent)}
    .plan .amount{font-family:var(--sl-font-display);font-size:44px;font-weight:700}
    .plan .amount small{font-family:var(--sl-font-base);font-size:15px;color:var(--sl-text-tertiary);font-weight:400}
    .plan ul{margin:0;padding:0;list-style:none;display:flex;flex-direction:column;gap:10px;font-size:15px}
    .plan li::before{content:"\2713\0020";color:var(--sl-success);font-weight:700}
    footer{padding:40px 6vw;background:#0F1218;color:#9BA3B7}
    footer .row{display:flex;align-items:center;gap:20px;flex-wrap:wrap}
    footer .row .fbrand{font-family:var(--sl-font-display);font-size:18px;font-weight:700;color:var(--sl-on-dark)}
    footer .row .flinks{margin-left:auto;display:flex;gap:20px}
    footer .row .flinks a{color:#C7D0EC;font-size:13.5px}
    footer .legal{font-size:12.5px;line-height:1.6;color:#6E7690;border-top:1px solid rgb(199 208 236 / .12);padding-top:16px;margin-top:18px}
    /* --- Dla kogo (2026-09-07) --- */
    .segs{display:grid;gap:20px;grid-template-columns:1fr}
    @media(min-width:760px){.segs{grid-template-columns:1fr 1fr}}
    .seg{background:var(--sl-surface);border:1px solid var(--sl-border);border-radius:16px;padding:26px;display:flex;flex-direction:column;gap:10px}
    .seg h3{font-family:var(--sl-font-display);font-size:21px;line-height:1.25;font-weight:700;margin:0}
    .seg p{font-size:15px;line-height:1.65;color:var(--sl-text-secondary);margin:0}
    /* --- Bezpieczenstwo (2026-09-07) --- */
    .dark .sub{color:var(--sl-on-dark-soft)}
    .dark .k{color:var(--sl-on-dark-accent)}
    .secgrid{display:grid;gap:20px;grid-template-columns:1fr;margin-bottom:46px}
    @media(min-width:760px){.secgrid{grid-template-columns:repeat(3,1fr)}}
    .sectile{border:1px solid rgb(199 208 236 / .18);border-radius:16px;padding:26px;background:rgb(199 208 236 / .05);display:flex;flex-direction:column;gap:8px}
    .sectile .big{font-family:var(--sl-font-display);font-size:30px;font-weight:700;line-height:1.15;color:var(--sl-on-dark)}
    .sectile .lbl{font-size:12px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:var(--sl-on-dark-accent)}
    .sectile p{font-size:14.5px;line-height:1.6;color:var(--sl-on-dark-soft);margin:0}
    .pillars{display:grid;gap:22px;grid-template-columns:1fr}
    @media(min-width:960px){.pillars{grid-template-columns:1fr 1fr}}
    .pillar{display:flex;gap:16px;align-items:flex-start}
    .pnum{font-family:var(--sl-font-display);font-size:15px;font-weight:700;color:var(--sl-on-dark-accent);border:1px solid rgb(147 180 255 / .3);border-radius:9999px;min-width:38px;height:38px;display:inline-flex;align-items:center;justify-content:center;flex-shrink:0}
    .pillar h3{font-family:var(--sl-font-display);font-size:19px;font-weight:700;margin:0 0 6px;color:var(--sl-on-dark)}
    .pillar p{font-size:14.5px;line-height:1.65;color:var(--sl-on-dark-soft);margin:0}
    .techlist{margin-top:36px;border:1px solid rgb(199 208 236 / .18);border-radius:14px}
    .techlist>summary{cursor:pointer;list-style:none;padding:16px 22px;font-size:15px;font-weight:600;color:var(--sl-on-dark)}
    .techlist>summary::-webkit-details-marker{display:none}
    .techlist>summary::after{content:" +";color:var(--sl-on-dark-accent);font-weight:700}
    .techlist[open]>summary::after{content:" \2212"}
    .techlist ul{margin:0;padding:0 22px 20px 42px;display:flex;flex-direction:column;gap:9px;font-size:14.5px;line-height:1.6;color:var(--sl-on-dark-soft)}
    /* --- FAQ (2026-09-07) --- */
    .faq{max-width:820px;margin:0 auto;display:flex;flex-direction:column;gap:12px}
    .faq details{background:var(--sl-surface);border:1px solid var(--sl-border);border-radius:12px}
    .faq summary{cursor:pointer;list-style:none;padding:18px 22px;font-size:16.5px;font-weight:600;color:var(--sl-text-primary)}
    .faq summary::-webkit-details-marker{display:none}
    .faq summary::after{content:"+";float:right;color:var(--sl-accent);font-weight:700}
    .faq details[open] summary::after{content:"\2212"}
    .faq p{margin:0;padding:0 22px 20px;font-size:15px;line-height:1.7;color:var(--sl-text-secondary)}
    /* --- CTA koncowe (2026-09-07) --- */
    .endcta{background:var(--sl-hero-gradient);color:var(--sl-on-dark);padding:82px 6vw;text-align:center}
    .endcta h2{max-width:22ch;margin:0 auto 14px}
    .endcta p{font-size:17px;line-height:1.65;color:var(--sl-on-dark-soft);max-width:58ch;margin:0 auto 30px}
    /* --- Spojnosc layoutu (2026-09-07) ---
       Diagnoza: sekcje nie mialy ZADNEGO ograniczenia szerokosci — tylko `padding:90px 6vw`.
       Na monitorze 1600px dawalo to ~1400px tresci, przez co „Dla kogo", „Bezpieczenstwo"
       i analiza dokumentow rozlewaly sie na caly ekran i wygladaly jak osobne strony.
       Cennik od poczatku byl scisniety do 880px i dlatego jako jedyny wygladal spojnie.
       Rozwiazanie: jedna miara tresci dla wszystkich sekcji + wyjatki tam, gdzie wezsza
       kolumna jest celowa (cennik, FAQ, akapity ciagle). */
    /* Kontener robimy PADDINGIEM sekcji, nie marginesami dzieci. Poprzednia wersja dawala
       `margin-inline:auto` kazdemu dziecku — przez co `.sub` (wezsza miara 72ch) centrowal sie
       wzgledem nagłowka i lewe krawedzie sie rozjezdzaly. Tutaj kolumna tresci jest jedna,
       a kazdy element zachowuje swoje wlasne wyrownanie: `.sub` do lewej, `.price`/`.faq`
       do srodka (maja wlasne `margin:0 auto`). */
    .light,.endcta{padding-left:max(6vw,(100% - 1180px) / 2);padding-right:max(6vw,(100% - 1180px) / 2)}
    .light>.k{display:block;margin-bottom:12px}
    .sech3{font-family:var(--sl-font-display);font-size:24px;font-weight:700;margin:0 0 24px}
    .dark .sech3{color:var(--sl-on-dark)}
    .candid{margin-top:34px;padding-top:22px;border-top:1px solid rgb(199 208 236 / .18);font-size:14.5px;line-height:1.65;color:var(--sl-on-dark-soft);max-width:70ch}
    /* Jeden rytm pionowy zamiast 90/82 px na sekcje; na telefonie sekcje byly nienaturalnie wysokie. */
    .light,.endcta{padding-top:84px;padding-bottom:84px}
    @media(max-width:720px){.light,.endcta{padding-top:56px;padding-bottom:56px}}
    </style></head><body>

    <div class="nav">
      <a class="brand" href="/start" style="text-decoration:none"><svg class="mark" viewBox="0 0 100 100" aria-hidden="true"><path d="M 60.94 19.93 A 32 32 0 1 1 39.06 19.93" fill="none" stroke="#EDEFF8" stroke-width="9" stroke-linecap="butt"/><circle cx="50" cy="18" r="5" fill="#D97706"/></svg><span class="omnia">Omnia</span><span class="si">SI</span></a>
      <span class="links"><a href="#roznice">Czym się różnimy</a><a href="#bezpieczenstwo">Bezpieczeństwo</a><a href="#cennik">Cennik</a><a href="/o-systemie">O systemie</a><!--NAV-CTA--></span>
      <details class="nav-burger">
        <summary aria-label="Menu">☰</summary>
        <nav class="nav-sheet"><a href="#roznice">Czym się różnimy</a><a href="#bezpieczenstwo">Bezpieczeństwo</a><a href="#cennik">Cennik</a><a href="/o-systemie">O systemie</a><!--NAV-CTA--></nav>
      </details>
    </div>

    <div class="hero">
      <div class="glow1"></div><div class="glow2"></div>
      <span class="eyebrow"><i></i>Asystent researchu prawnego · dane i modele w UE</span>
      <h1>Zna źródła<br>każdej swojej odpowiedzi.</h1>
      <p class="lead">Przepisy z pilnowaniem nowelizacji, orzecznictwo, cytowania do zweryfikowania jednym kliknięciem — a gdy źródła nie wystarczają, OmniaSI mówi to wprost, zamiast zgadywać.</p>
      <div class="heroctas"><!--CTA--><a class="btn-line" href="#roznice">Czym się różnimy</a></div>
      <p class="quiet">Twoje pytania i dokumenty nie trenują żadnego modelu.</p>
    </div>

    <div class="light" id="roznice">
      <h2>Ogólny chatbot odpowie na wszystko.<br>OmniaSI odpowiada za coś.</h2>
      <p class="sub">Cztery rzeczy, których nie dostaniesz od uniwersalnego czatu — a które rozstrzygają o tym, czy research nadaje się do pracy.</p>
      <div class="bento">
        <div class="cardw">
          <span class="k">Nowelizacje pod kontrolą</span>
          <h3>To samo pytanie o zmieniony przepis. Zobacz różnicę.</h3>
          <span class="q"><strong>Pytanie:</strong> [PYTANIE O PRZEPIS OBJĘTY ŚWIEŻĄ NOWELIZACJĄ]</span>
          <div class="cmp">
            <div class="ans ans-bad">
              <span class="who">Gemini <span class="tag" style="background:var(--sl-error)">NIEAKTUALNY STAN PRAWNY</span></span>
              <span>[ODPOWIEDŹ OGÓLNEGO CZATU — pewna siebie, oparta na brzmieniu przepisu sprzed nowelizacji, bez źródeł do sprawdzenia]</span>
            </div>
            <div class="ans ans-good">
              <span class="who">OmniaSI <span class="tag" style="background:var(--sl-warning)">NOWELIZACJA — WEJDZIE W ŻYCIE [DATA]</span></span>
              <span>[ODPOWIEDŹ OMNIASI — zestawia dotychczasowy i nowy stan prawny, podaje dokładną datę wejścia zmiany w życie] <span class="cite">1</span> <span class="cite">2</span></span>
            </div>
          </div>
          <span class="foot-note">Porównanie na rzeczywistym pytaniu — odpowiedzi z [DATA POROWNANIA], pełne zrzuty na życzenie.</span>
        </div>
        <div class="cardw dark">
          <span class="k" style="color:var(--sl-on-dark-accent)">Twoje sprawy zostają Twoje</span>
          <h3>Pytania i dokumenty nie trenują żadnego modelu.</h3>
          <p>Całość działa na infrastrukturze w Unii Europejskiej — bez wysyłania danych za ocean. To argument dla tajemnicy zawodowej, którego nie daje research na amerykańskim API.</p>
        </div>
        <div class="cardw">
          <span class="k">Uczciwa odmowa</span>
          <h3>Woli powiedzieć „nie wiem" niż zmyślić przepis.</h3>
          <p>Każda odpowiedź przechodzi walidację cytowań — a brak podstaw w źródłach kończy się jawną odmową, nie zmyśloną sygnaturą podaną z pełnym przekonaniem.</p>
        </div>
        <div class="cardw">
          <span class="k">Wszystko ze źródeł</span>
          <h3>Każda teza z cytowaniem, każde cytowanie do sprawdzenia.</h3>
          <p>Kodeksy, ustawy i rozporządzenia (ISAP), prawo Unii (EUR-Lex) oraz orzecznictwo SN, sądów powszechnych i administracyjnych — źródło otwierasz jednym kliknięciem <span class="cite" style="background:var(--sl-accent-light)">1</span>.</p>
        </div>
      </div>

      <div class="table">
        <h3>To samo pytanie, dwa różne narzędzia</h3>
        <div class="trow h"><span></span><span class="c" style="color:var(--sl-on-dark-accent)">OmniaSI</span><span class="c">Ogólny chatbot</span></div>
        <div class="trow"><span>Odpowiedź wyłącznie ze źródeł, z cytowaniami do weryfikacji</span><span class="c yes">✓</span><span class="c no">✕</span></div>
        <div class="trow"><span>Pilnowanie nowelizacji: która wersja przepisu obowiązuje dziś</span><span class="c yes">✓</span><span class="c no">✕</span></div>
        <div class="trow"><span>Przyznaje się, gdy nie ma podstaw do odpowiedzi</span><span class="c yes">✓</span><span class="c no">✕</span></div>
        <div class="trow"><span>Pytania i dokumenty nie trenują modelu; dane w UE</span><span class="c yes">✓</span><span class="c no">zależnie od planu</span></div>
      </div>
    </div>

    <div class="light" style="background:var(--sl-surface)">
      <div class="analys">
        <div>
          <span class="k">Analiza dokumentów</span>
          <h2>Wgraj umowę.<br>Dostaniesz analizę paragraf po paragrafie.</h2>
          <p class="sub" style="margin-bottom:20px">Ty ustawiasz kierunek pytaniem — np. <em>„oceń ryzyka dla najemcy"</em> — a OmniaSI ocenia dokument fragment po fragmencie, zderzając każdy z przepisami i orzecznictwem.</p>
          <div class="checks">
            <span>Werdykt dla każdego fragmentu: OK / ryzyko / brak źródeł do oceny</span>
            <span>Uzasadnienie z cytowaniami przy każdej uwadze + streszczenie całości</span>
            <span>Treść dokumentu nie jest nigdzie zapisywana — przechowujemy tylko raport</span>
          </div>
        </div>
        <div class="doc">
          <strong style="font-size:14.5px;color:var(--sl-text-primary)">umowa-najmu-lokalu.pdf <span style="font-weight:400;color:var(--sl-text-tertiary)">· 12 fragmentów</span></strong>
          <div class="unit"><span class="u-pill" style="background:var(--sl-success-bg);color:var(--sl-success)">§ 4 · OK</span>Czynsz i termin płatności określone jednoznacznie — zgodnie z wymogami k.c. <span class="cite" style="background:var(--sl-accent-light)">1</span></div>
          <div class="unit" style="border:1.5px solid var(--sl-error-border);background:var(--sl-error-bg);color:var(--sl-text-primary)"><span class="u-pill" style="background:var(--sl-error);color:#fff">§ 7 · RYZYKO</span>Kara umowna bez górnej granicy — w orzecznictwie uznawana za rażąco wygórowaną i podlegającą miarkowaniu <span class="cite">2</span> <span class="cite">3</span></div>
          <div class="unit" style="background:var(--sl-warning-bg)"><span class="u-pill" style="background:var(--sl-warning);color:#fff">§ 11 · BRAK ŹRÓDEŁ</span>Źródła nie pozwalają ocenić tego fragmentu — OmniaSI mówi to wprost zamiast zgadywać</div>
        </div>
      </div>
    </div>

    <div class="light" id="dla-kogo" style="background:var(--sl-bg)">
      <span class="k">Dla kogo</span>
      <h2>Research, który da się sprawdzić</h2>
      <p class="sub">Niezależnie od tego, w jakiej roli sprawdzasz — mechanizm jest ten sam: teza, cytat, źródło do otwarcia.</p>
      <div class="segs">
        <div class="seg">
          <span class="k">Szybszy pierwszy krok w sprawie</span>
          <h3>Małe i średnie kancelarie</h3>
          <p>Pytasz po polsku, dostajesz przepisy i orzeczenia z sygnaturami do sprawdzenia. Zamiast godziny w wyszukiwarce — kilka minut i lista źródeł, od której zaczynasz pracę.</p>
        </div>
        <div class="seg">
          <span class="k">Prawo polskie i unijne w jednym pytaniu</span>
          <h3>Prawnicy in-house i compliance</h3>
          <p>RODO, AI Act, DSA, DORA — akty unijne obok polskiej ustawy wdrażającej, w jednej odpowiedzi. Bez przeskakiwania między EUR-Leksem a ISAP-em.</p>
        </div>
        <div class="seg">
          <span class="k">Wiesz, co się zmieniło i od kiedy</span>
          <h3>Praktycy w obszarach po nowelizacji</h3>
          <p>Prawo pracy, procedura, podatki — tam gdzie tekst jednolity nie nadąża. OmniaSI pokazuje nowelizację, której jeszcze nie ma w tekście, i datę jej wejścia w życie.</p>
        </div>
        <div class="seg">
          <span class="k">Każda teza do zweryfikowania</span>
          <h3>Aplikanci i młodsi prawnicy</h3>
          <p>Nie musisz wierzyć odpowiedzi — otwierasz źródło i sprawdzasz. Plan Start (0 zł) wystarcza, żeby zobaczyć, czy to działa na Twoich sprawach.</p>
        </div>
      </div>
    </div>

    <div class="light dark" id="bezpieczenstwo">
      <span class="k">Bezpieczeństwo</span>
      <h2>Tajemnica zawodowa nie znosi wyjątków dla wygody.</h2>
      <p class="sub">Za poufność wobec klienta odpowiadasz Ty, niezależnie od tego, jakiego narzędzia używasz. Dlatego OmniaSI jest zbudowana tak, żeby jak najmniej Twoich danych w ogóle powstawało.</p>
      <div class="secgrid">
        <div class="sectile">
          <span class="lbl">Lokalizacja danych</span>
          <span class="big">Unia Europejska</span>
          <p>Baza, aplikacja i model językowy działają na infrastrukturze w UE. Twoje pytania nie wyjeżdżają za ocean — także zanonimizowane.</p>
        </div>
        <div class="sectile">
          <span class="lbl">Treść dokumentów</span>
          <span class="big">Nie zapisujemy</span>
          <p>Wgrana umowa żyje tylko na czas analizy. Do bazy trafia raport z werdyktami i cytatami — nie treść dokumentu.</p>
        </div>
        <div class="sectile">
          <span class="lbl">Uprawnienia modelu</span>
          <span class="big">Zero narzędzi</span>
          <p>Model czyta źródła i odpowiada. Nie wysyła wiadomości, nie wywołuje usług, nie zapisuje niczego poza odpowiedzią.</p>
        </div>
      </div>

      <h3 class="sech3">Jak chronimy Twoje dane</h3>
      <div class="pillars">
        <div class="pillar">
          <span class="pnum">01</span>
          <div>
            <h3>Dokument, którego nie ma, nie wycieknie</h3>
            <p>Nie budujemy repozytorium akt kancelarii. Wgrany plik jest przetwarzany na czas analizy i znika — nie trafia na dysk ani do bazy danych. To najtańsze zabezpieczenie, jakie istnieje: dane, których nie przechowujemy, nie mogą zostać ujawnione.</p>
          </div>
        </div>
        <div class="pillar">
          <span class="pnum">02</span>
          <div>
            <h3>Odgrodzone konta, sprawdzone testami</h3>
            <p>Rozmowy i analizy są przypisane do właściciela i sprawdzane przy każdym odczycie, a zapytania do bazy są w całości parametryczne. Izolacja kont ma pokrycie w testach automatycznych, nie w deklaracji na stronie.</p>
          </div>
        </div>
        <div class="pillar">
          <span class="pnum">03</span>
          <div>
            <h3>Audyt, którego wyniki pokazujemy</h3>
            <p>Przeszliśmy audyt wobec OWASP Top 10 dla aplikacji LLM — z listą ustaleń, ścieżkami w kodzie i statusem napraw. Nie każde ustalenie było wygodne; te o najwyższej wadze zostały naprawione i pokryte testami.</p>
          </div>
        </div>
        <div class="pillar">
          <span class="pnum">04</span>
          <div>
            <h3>Pytania nie trenują modelu</h3>
            <p>Twoje zapytania i dokumenty nie są wykorzystywane do trenowania ani ulepszania modeli językowych. Nie są też walutą, którą płacisz za korzystanie z narzędzia.</p>
          </div>
        </div>
      </div>

      <details class="techlist">
        <summary>Szczegóły techniczne</summary>
        <ul>
          <li>Ścisła polityka bezpieczeństwa treści (CSP <code>default-src 'self'</code>, <code>frame-ancestors 'none'</code>) — strona nie ładuje skryptów ani czcionek z obcych serwerów.</li>
          <li>Nagłówki ochronne: <code>X-Frame-Options: DENY</code>, <code>X-Content-Type-Options: nosniff</code>, <code>Referrer-Policy: strict-origin-when-cross-origin</code>.</li>
          <li>Odpowiedzi modelu renderowane przez sanityzację HTML — treść z modelu nie może wykonać kodu w Twojej przeglądarce.</li>
          <li>Zapytania do bazy wyłącznie parametryczne; brak sklejania SQL z tekstu.</li>
          <li>Limity zapytań na poziomie konta i całej instancji — pojedynczy użytkownik nie jest w stanie wyczerpać usługi dla pozostałych.</li>
          <li>Klucze szyfrowania sesji trzymane poza obrazem aplikacji; ruch szyfrowany TLS na wejściu.</li>
        </ul>
      </details>

      <p class="candid">Jeśli Twoja kancelaria wymaga certyfikatu w procesie zakupowym — napisz do nas, to jest właściwy moment, żeby to zaplanować.</p>
    </div>

    <div class="light" id="cennik">
      <h2 style="text-align:center">Prosty cennik</h2>
      <p class="sub" style="text-align:center;margin-inline:auto">Zacznij za darmo. Przejdź wyżej, gdy research stanie się codziennością.</p>
      <div class="price">
        <div class="plan">
          <span class="name">Start</span>
          <span class="amount">0 zł <small>/ miesiąc</small></span>
          <ul><li>15 zapytań miesięcznie</li><li>Pełna baza przepisów i orzecznictwa</li><li>Cytowania, panel źródeł, nowelizacje</li></ul>
          <!--CTA-START-->
        </div>
        <div class="plan pro">
          <span class="badge-top">DLA PRAKTYKI</span>
          <span class="name">Pro</span>
          <span class="amount">[CENA] zł <small>/ miesiąc</small></span>
          <ul><li>300 zapytań miesięcznie</li><li>Wszystko z planu Start + analiza dokumentów</li><li>Anulujesz w każdej chwili — plan działa do końca okresu</li></ul>
          <!--CTA-PRO-->
        </div>
      </div>
    </div>

    <div class="light" id="faq" style="background:var(--sl-surface)">
      <h2 style="text-align:center">Często zadawane pytania</h2>
      <p class="sub" style="text-align:center;margin-inline:auto;margin-bottom:36px">Sześć pytań, które słyszymy najczęściej — z odpowiedziami bez marketingowej waty.</p>
      <div class="faq">
        <details>
          <summary>Czym to się różni od ChatGPT albo Gemini?</summary>
          <p>Ogólny czat odpowiada z pamięci modelu i brzmi tak samo pewnie wtedy, gdy ma rację, i wtedy, gdy jej nie ma. OmniaSI najpierw znajduje przepis lub orzeczenie w korpusie, a dopiero potem odpowiada — wyłącznie na podstawie znalezionych fragmentów, z cytatem, który otwierasz w oryginale. Gdy nie znajdzie podstawy, mówi o tym wprost.</p>
        </details>
        <details>
          <summary>Z jakich źródeł korzysta?</summary>
          <p>Kodeksy, ustawy i rozporządzenia z ISAP/ELI, prawo Unii Europejskiej z EUR-Lex (od 2004 r.) oraz orzecznictwo Sądu Najwyższego, sądów powszechnych i administracyjnych (SAOS). Wszystko z publicznych, państwowych baz — bez treści licencjonowanych od komercyjnych wydawców.</p>
        </details>
        <details>
          <summary>Co się dzieje z moimi pytaniami i dokumentami?</summary>
          <p>Nie trenują żadnego modelu. Wgrany dokument nie trafia do naszej bazy — żyje tylko na czas analizy, a zapisujemy raport, nie treść umowy. Rozmowy i raporty starsze niż sześć miesięcy kasują się automatycznie. Więcej w sekcji <a href="#bezpieczenstwo">Bezpieczeństwo</a>.</p>
        </details>
        <details>
          <summary>Co, jeśli wgrany dokument zawiera ukryte polecenia dla AI?</summary>
          <p>To realne ryzyko każdego narzędzia, które czyta cudze pliki, i nie udajemy, że go nie ma — nazwaliśmy je wprost w naszym audycie bezpieczeństwa. Ograniczamy skutki architekturą: model nie ma żadnych narzędzi, którymi mógłby takie polecenie wykonać, a treść Twojego dokumentu jest cytowana w innej przestrzeni niż przepisy — dokument nie może stać się podstawą prawną odpowiedzi. Każdy werdykt w raporcie jest przypisany do konkretnego fragmentu, więc widzisz, na czym analiza się oparła.</p>
        </details>
        <details>
          <summary>Co robi, gdy nie zna odpowiedzi?</summary>
          <p>Mówi „nie znalazłem jednoznacznej podstawy prawnej" zamiast zgadywać. To jest funkcja, nie awaria: research, który czasem odmawia, jest użyteczniejszy niż taki, przy którym nigdy nie wiadomo, kiedy mu wierzyć.</p>
        </details>
        <details>
          <summary>Czy to zastępuje poradę prawną?</summary>
          <p>Nie. OmniaSI przygotowuje research do weryfikacji przez prawnika. Dlatego każda teza ma cytat — ostatnie słowo należy do Ciebie, nie do modelu.</p>
        </details>
        <details>
          <summary>Czy mogę przetestować przed płaceniem?</summary>
          <p>Tak. Plan Start to 0 zł i 15 pytań miesięcznie, bez podawania karty. Wystarczy, żeby zadać własne pytania z własnych spraw — a to jedyny test, który cokolwiek rozstrzyga.</p>
        </details>
      </div>
    </div>

    <div class="endcta">
      <h2>Zadaj pierwsze pytanie z własnej sprawy</h2>
      <p>Nie z demo, nie z przykładu — z tego, nad czym pracujesz dziś. To jedyny test, który cokolwiek rozstrzyga.</p>
      <div class="heroctas"><!--CTA-END--><a class="btn-line" href="/o-systemie">Zobacz, jak to działa</a></div>
    </div>

    <footer>
      <div class="row">
        <span class="fbrand">OmniaSI</span>
        <span class="flinks"><a href="#faq">FAQ</a><a href="/regulamin">Regulamin</a><a href="/prywatnosc">Polityka prywatności</a><a href="/cookies">Cookies</a><a href="/o-systemie">O systemie</a></span>
      </div>
      <div class="legal">OmniaSI generuje research prawny do weryfikacji przez prawnika — nie świadczy porad prawnych. Treści generowane przez sztuczną inteligencję są oznaczane maszynowo zgodnie z aktem o sztucznej inteligencji (AI Act).</div>
    </footer>
    <!--ANALYTICS-->
    </body></html>
    """;

// Wezwanie do działania na landingu zależy od trybu: konta → rejestracja, alfa → kod zaproszenia.
// CTA zależne od trybu: konta → rejestracja/logowanie, alfa → kod zaproszenia (jak dotąd).
// Analityka bez cookies (US-2.12): snippet Umami podstawiany do wszystkich wariantów landingu;
// pusty, gdy Analytics nieskonfigurowane.
var landingBase = LandingHtml.Replace("<!--ANALYTICS-->", AnalyticsSnippet.Html);
var landingHtml = authOptions.Enabled
    ? landingBase
        .Replace("<!--NAV-CTA-->", """<a class="btn-line" href="/logowanie" style="min-height:40px">Zaloguj się</a><a class="btn" href="/rejestracja" style="min-height:40px" data-umami-event="cta-nav-rejestracja">Wypróbuj za darmo</a>""")
        .Replace("<!--CTA-->", """<a class="btn" href="/rejestracja" style="min-height:52px;padding:0 30px;font-size:16.5px" data-umami-event="cta-hero-rejestracja">Zacznij za darmo — 15 pytań/mies.</a>""")
        .Replace("<!--CTA-START-->", """<a class="btn-line" href="/rejestracja" style="color:var(--sl-text-primary);border-color:var(--sl-border);margin-top:auto;justify-content:center" data-umami-event="cta-plan-start">Załóż konto</a>""")
        .Replace("<!--CTA-PRO-->", """<a class="btn" href="/rejestracja" style="margin-top:auto;justify-content:center" data-umami-event="cta-plan-pro">Wybierz Pro</a>""")
        .Replace("<!--CTA-END-->", """<a class="btn" href="/rejestracja" style="min-height:52px;padding:0 30px;font-size:16.5px" data-umami-event="cta-koncowe-rejestracja">Zacznij za darmo — 15 pytań/mies.</a>""")
    : landingBase
        .Replace("<!--NAV-CTA-->", """<a class="btn" href="/wejscie" style="min-height:40px">Mam kod zaproszenia</a>""")
        .Replace("<!--CTA-->", """<a class="btn" href="/wejscie" style="min-height:52px;padding:0 30px;font-size:16.5px">Mam kod zaproszenia → Wejdź</a>""")
        .Replace("<!--CTA-START-->", """<a class="btn-line" href="/wejscie" style="color:var(--sl-text-primary);border-color:var(--sl-border);margin-top:auto;justify-content:center">Zamknięty test — mam kod</a>""")
        .Replace("<!--CTA-PRO-->", """<span class="foot-note" style="margin-top:auto">Dostępne po starcie publicznym.</span>""")
        .Replace("<!--CTA-END-->", """<a class="btn" href="/wejscie" style="min-height:52px;padding:0 30px;font-size:16.5px">Mam kod zaproszenia → Wejdź</a>""");

// Landing dla ZALOGOWANEGO (leftover 2026-08-31: logo ma wracać na stronę główną, a `/` dla
// zalogowanych nadal przekierowuje do /czat — inwariant zostaje). /start to jawne „pokaż stronę
// główną" spod logo; CTA prowadzą wtedy do aplikacji/konta, nie do rejestracji.
var landingHtmlAuthed = landingBase
    .Replace("<!--NAV-CTA-->", """<a class="btn" href="/czat" style="min-height:40px">Przejdź do czatu</a><!--WHO-->""")
    .Replace("<!--CTA-->", """<a class="btn" href="/czat" style="min-height:52px;padding:0 30px;font-size:16.5px">Przejdź do czatu</a>""")
    .Replace("<!--CTA-END-->", """<a class="btn" href="/czat" style="min-height:52px;padding:0 30px;font-size:16.5px">Przejdź do czatu</a>""")
    .Replace("<!--CTA-START-->", """<span class="foot-note" style="margin-top:auto">Masz już konto.</span>""")
    .Replace("<!--CTA-PRO-->", billingOptions.Enabled
        ? """<a class="btn" href="/konto" style="margin-top:auto;justify-content:center">Zarządzaj planem</a>"""
        : """<span class="foot-note" style="margin-top:auto">Dostępne po starcie publicznym.</span>""");

app.MapGet("/", (HttpContext http) =>
    http.User.Identity?.IsAuthenticated == true
        ? Results.Redirect("/czat")
        : Results.Content(landingHtml, "text/html; charset=utf-8"));

app.MapGet("/start", (HttpContext http) =>
{
    if (http.User.Identity?.IsAuthenticated != true)
        return Results.Content(landingHtml, "text/html; charset=utf-8");

    // Chip konta jak w nagłówku aplikacji (spójność 2026-09-01) — inicjał z imienia (claim
    // GivenName z AppClaimsFactory), a gdy go nie ma, z e-maila. Podstawiane per żądanie,
    // bo landing poza tym jest statycznym stringiem policzonym na starcie.
    var given = http.User.FindFirstValue(ClaimTypes.GivenName);
    var who = string.IsNullOrWhiteSpace(given)
        ? http.User.FindFirstValue(ClaimTypes.Email) ?? http.User.Identity.Name ?? ""
        : given;
    var initial = string.IsNullOrWhiteSpace(who) ? "?" : char.ToUpperInvariant(who[0]).ToString();
    var title = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(who);
    var chip = billingOptions.Enabled
        ? $"""<a class="navwho" href="/konto" title="Konto — {title}"><span class="navavatar">{initial}</span></a>"""
        : $"""<span class="navwho" title="{title}"><span class="navavatar">{initial}</span></span>""";
    return Results.Content(landingHtmlAuthed.Replace("<!--WHO-->", chip), "text/html; charset=utf-8");
});

// Dokumenty prawne (RED-4.7 → treść 2026-09-01): regulamin, polityka prywatności i cookies renderowane
// z plików Legal/*.md wbudowanych w assembly (LegalPages). Do czasu weryfikacji prawnej treść zawiera
// pola w nawiasach kwadratowych do uzupełnienia (podmiot, adres, dostawcy) — patrz
// docs/ANALIZA-DOKUMENTY-PRAWNE-2026-09-01.md. Bez snippetu analityki: strony prawne nie mierzą ruchu.
foreach (var legalDoc in LegalPages.All)
    app.MapGet($"/{legalDoc.Slug}", () => Results.Content(LegalPages.GetHtml(legalDoc), "text/html; charset=utf-8"));

// Konta (E1, blok A) — mapowane TYLKO gdy włączone; inaczej zostaje bramka na kody zaproszeń.
if (authOptions.Enabled) app.MapAuthEndpoints();

// Płatności (E3/US-3.1 — spike). Bez kont nie ma czego subskrybować, więc wymagają Auth:Enabled.
if (billingOptions.Enabled) app.MapBillingEndpoints();

if (!authOptions.Enabled)
{
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
} // koniec bramki na kody zaproszeń (tylko gdy Auth:Enabled=false)

// Autoryzacja API: cookie ALBO nagłówek X-Invite-Code (wygoda curl/runbooków). Zwraca tożsamość testera
// (nazwę) do limitów, albo null = odmowa. Gdy bramka wyłączona — placeholder jak dotąd.
string? ResolveApiUser(HttpContext http)
{
    // Zalogowany: tożsamością jest identyfikator konta (parytet z ICurrentUser — ten sam klucz
    // trafia do rozmów i do liczników). Dla bramki invite claim ten niesie nazwę testera.
    if (http.User?.Identity?.IsAuthenticated == true)
        return UserIdentity.KeyOf(http.User);

    if (authOptions.Enabled) return null;      // konta włączone → API tylko dla zalogowanych
    if (!access.Enabled) return "demo@local";  // dev/M4 bez żadnej bramki
    return access.TryResolveInvite(http.Request.Headers["X-Invite-Code"], out var tester) ? tester : null;
}

// --- Retrieval (debug / panel źródeł E4) ---
app.MapPost("/api/search", async (HttpContext http, SearchRequest req, IRetriever retriever, IOptions<RetrievalOptions> opt, CancellationToken ct) =>
{
    if (ResolveApiUser(http) is null) return Results.Unauthorized(); // bramka 3.7 (cookie lub X-Invite-Code)
    // Wyszukiwarka: sąsiedztwo (plan SAS) WYŁĄCZONE. Tam wynikiem jest lista trafień do przejrzenia
    // przez człowieka — dociąganie sąsiednich artykułów rozmyłoby ranking i zalało listę przepisami,
    // których nikt nie szukał. Mechanizm istnieje po to, żeby MODEL widział przepis pod nazwą,
    // której użytkownik nie zna; człowiek na liście wyników tego nie potrzebuje.
    var result = await retriever.RetrieveAsync(
        ToQuery(req.Query, req.Filters, req.TopK ?? opt.Value.TopK, opt.Value)
            with { NeighbourhoodRadius = 0 }, ct);
    return Results.Ok(new
    {
        maxSimilarity = result.MaxSimilarity,
        wouldAbstain = AbstentionPolicy.ShouldAbstain(result, opt.Value.AbstentionThreshold),
        chunks = result.Chunks.Select(c => new { c.Text, c.Section, c.Source, c.Title, c.SourceUrl, c.Score, c.Similarity, locator = GroundedPrompt.LocatorLabel(c) }),
    });
}).RequireRateLimiting("api");

// --- Chat z ugruntowaniem (SSE) ---
// JEDNA implementacja z torem Blazora: endpoint tylko tłumaczy strumień ChatEvent z IChatService na
// ramki SSE (kontrakt zdarzeń: ChatSse). Do audytu 2026-09-01 (W1) miał tu własną, uproszczoną kopię
// pipeline'u — bez bramki anty-fabrykacji (AnswerGate), pętli domykającej i routera — więc odpowiedź
// z wymyślonym artykułem wychodziła w całości, a wynik CitationValidator był tylko flagą w `done`.
app.MapPost("/api/chat", async (HttpContext http, ChatRequest req, IChatService chat, IOptions<DiagnosticsOptions> diag, CostGuard costGuard, CancellationToken ct) =>
{
    // Bramka 3.7: tożsamość (cookie lub X-Invite-Code) PRZED otwarciem streamu — 401 zamiast SSE.
    if (ResolveApiUser(http) is not { } apiUser) return Results.Unauthorized();

    // Tor czatu nie zna filtrów retrievalu (ChatService buduje zapytanie bez CourtType/dat/OnlyInForce —
    // tak samo jak UI). Jawny 400 zamiast cichego ignorowania; filtrowana lista trafień to /api/search.
    if (req.Filters is { } f && (f.CourtType is not null || f.DateFrom is not null || f.DateTo is not null || f.OnlyInForce))
        return Results.BadRequest(new { message = "Filtry nie są obsługiwane w /api/chat (ten sam tor co czat UI) — użyj /api/search." });

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(SseFrame frame)
    {
        await http.Response.WriteAsync($"event: {frame.Event}\ndata: {JsonSerializer.Serialize(frame.Data, json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    // Licznik znaków odpowiedzi do dobowego budżetu pojemności — zerowany przy regeneracji/drugiej
    // rundzie, jak `ex.Answer = ""` w Chat.razor (liczymy to, co użytkownik ostatecznie dostał).
    var answerChars = 0;
    try
    {
        // Limit planu + capy pojemności (obok rate-limitera HTTP) — parytet z UI/Chat.razor.
        if (await costGuard.TryAcquireAsync(apiUser, ct) is { Allowed: false } limit)
        {
            await Send(new("error", new { message = limit.Message }));
            await Send(new("done", new { abstained = true }));
            return Results.Empty;
        }

        var history = (req.History ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Question))
            .Select(t => new ChatTurn(t.Question, t.Answer))
            .ToList();

        // Ten sam AskAsync co Chat.razor: router, follow-upy, bramka abstynencji, augmenter,
        // pętla domykająca, AnswerGate (regeneracja → odmowa), oznaczenie AI Act — wszystko w środku.
        await foreach (var evt in chat.AskAsync(req.Question, history, document: null, forceRetrieval: false, ct))
        {
            if (ChatSse.ResetsAnswer(evt)) answerChars = 0;
            if (evt is TokenEvent t) answerChars += t.Text.Length;
            await Send(ChatSse.Map(evt, diag.Value.ShowTokenUsage));
        }
        return Results.Empty;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.Empty; // klient odszedł — nie ma komu pisać
    }
    catch (Exception ex)
    {
        await Send(new("error", new { message = ex.Message }));
        return Results.Empty;
    }
    finally
    {
        // Jak w Chat.razor: doliczenie wyjścia jest best-effort i nie może wywalić odpowiedzi.
        try { await costGuard.RecordAsync(apiUser, answerChars, CancellationToken.None); } catch { }
    }
}).RequireRateLimiting("api");

// Ochrona UI: niezalogowany → 302 na stronę logowania (LoginPath cookie handlera). Landing, strony
// prawne i same formularze logowania są mapowane osobno (minimalne API), więc zostają anonimowe.
var components = app.MapRazorComponents<PrawoRAG.Api.Components.App>().AddInteractiveServerRenderMode();
if (access.Enabled || authOptions.Enabled) components.RequireAuthorization();

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
    NeighbourhoodRadius = o.NeighbourhoodRadius,
    NeighbourhoodMinChunks = o.NeighbourhoodMinChunks,
    NeighbourhoodTokenBudget = o.NeighbourhoodTokenBudget,
    VacatioLegisChunks = o.VacatioLegisChunks,
};

internal sealed record FiltersDto(string? CourtType, DateOnly? DateFrom, DateOnly? DateTo, bool OnlyInForce = false);
internal sealed record SearchRequest(string Query, FiltersDto? Filters, int? TopK);
internal sealed record ChatRequest(string Question, FiltersDto? Filters, IReadOnlyList<HistoryTurnDto>? History = null);

/// <summary>Jedna zakończona tura rozmowy w żądaniu SSE (kontekst follow-upów). Answer=null przy abstynencji.
/// Kotwice źródeł USUNIĘTE 2026-08-11 (patrz <see cref="ChatTurn"/>) — kontekstualizacja follow-upu
/// folduje wyłącznie cytaty i fragment tekstu odpowiedzi.</summary>
internal sealed record HistoryTurnDto(string Question, string? Answer);

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 8;
    public int CandidatesPerPath { get; set; } = 50;
    public double AbstentionThreshold { get; set; } = AbstentionPolicy.DefaultThreshold;

    /// <summary>Próg wyzwalający DRUGĄ rundę <see cref="PrawoRAG.Domain.Retrieval.GapClosingRetrieval"/> —
    /// celowo osobny od <see cref="AbstentionThreshold"/> (patrz doc parametru
    /// <c>gapClosingThreshold</c> w tamtej klasie: dzielenie jednej zmiennej ustawionej na 0
    /// unieruchomiło drugą rundę niemal całkowicie, 2026-08-25).</summary>
    public double GapClosingTriggerThreshold { get; set; } = AbstentionPolicy.DefaultThreshold;

    /// <summary>Minimalna liczba tokenów chunka w retrievalu (odsiew zdegenerowanych mini-chunków).</summary>
    public int MinChunkTokens { get; set; } = 20;

    /// <summary>Ile fragmentów pobiera strona „Wyszukiwarka" (retrieval-only, bez LLM). Większe niż
    /// czatowe TopK=8, bo wyniki grupujemy po dokumencie — chcemy pokryć kilkanaście–kilkadziesiąt
    /// dokumentów. Strojenie bez redeployu.</summary>
    public int SearchTopK { get; set; } = 25;

    /// <summary>Margines sygnału przy follow-upach: surowe dopytanie musi pobić wariant kontekstowy
    /// o tyle, żeby wygrać (różnice rzędu 1e-6 to szum — patrz <see cref="FollowUpQuery"/>).</summary>
    public double FollowUpSignalMargin { get; set; } = FollowUpQuery.DefaultSignalMargin;

    /// <summary>Margines sygnału przy follow-upach na skali cross-encodera (używany, gdy
    /// Reranker:Enabled=true i OBA warianty mają score). Inna skala niż
    /// <see cref="FollowUpSignalMargin"/> — patrz <see cref="FollowUpQuery.DefaultRerankSignalMargin"/>.</summary>
    public double RerankSignalMargin { get; set; } = FollowUpQuery.DefaultRerankSignalMargin;

    /// <summary>
    /// Router intencji (Faza 2 planu ROU): gdy <c>false</c> — KAŻDA wiadomość idzie do retrievalu,
    /// czyli zachowanie bajt w bajt jak przed wprowadzeniem routera. Domyślnie WYŁĄCZONY do czasu
    /// pełnej weryfikacji E2E (Zadanie 17), bo to jedyna zmiana w tym planie, która może sprawić,
    /// że odpowiedź powstanie bez źródeł — jego trafność po stronie krytycznej musi być zmierzona
    /// (100% pytań prawnych do bazy), a nie założona.
    /// </summary>
    public bool RouterEnabled { get; set; }

    /// <summary>
    /// Pętla domykająca lukę (Faza 4 planu ROU): gdy retrieval nie daje pokrycia, druga runda
    /// z zapytaniem przełożonym na terminologię ustawową. Domyślnie WŁĄCZONA — może tylko DODAĆ
    /// kontekst (bramki działają na końcu bez zmian), więc nie wnosi nowego ryzyka halucynacji,
    /// a jej koszt płacą wyłącznie pytania, które dziś kończą się odmową.
    /// </summary>
    public bool GapClosingEnabled { get; set; } = true;

    /// <summary>
    /// Maksymalna liczba DODATKOWYCH rund retrievalu w turze. 1 = jedna druga próba; 0 = zachowanie
    /// jak przed Fazą 4. Wyżej świadomie nie idziemy: przy trzeciej rundzie tura kosztuje kilka minut,
    /// a plan mówi wprost, że jeśli druga runda ratuje &lt;20% odmów, to problemem jest korpus
    /// albo słownik synonimów — nie liczba prób.
    /// </summary>
    public int MaxExtraRounds { get; set; } = 1;

    /// <summary>
    /// Tool calling (Faza 5 planu ROU): model sam formułuje zapytania do bazy przez narzędzie
    /// <c>szukaj_w_przepisach</c>.
    ///
    /// Domyślnie WYŁĄCZONY i taki ma zostać także po integracji — reguła R1 planu. Dla POJEDYNCZEGO
    /// pytania („Czy aplikant adwokacki może zastępować radcę prawnego?") tool calling nie dodaje
    /// wartości: zapytanie, które model by sformułował, jest praktycznie tożsame z pytaniem
    /// użytkownika. Dodaje natomiast jedno pełne wywołanie modelu głównego, czyli przy ~41 s
    /// rozumowania podwaja najdroższą operację w systemie. Zarabia na siebie dopiero tam, gdzie model
    /// ITERUJE — czyli w przyszłej pracy agentowej (analiza i generowanie pism), nie w typowym Q&amp;A.
    /// </summary>
    public bool ToolCallingEnabled { get; set; }

    /// <summary>Górny limit wywołań narzędzia w turze — twardy hamulec na koszt.</summary>
    public int MaxToolCalls { get; set; } = 2;

    /// <summary>
    /// Rozszerzenie sąsiedztwa artykułów w dominującym akcie (plan SAS) — ile artykułów w każdą
    /// stronę. 0 = wyłączone. Powód: retrieval trafia w AKT i mija właściwy przepis, bo ten nazywa
    /// się inaczej niż w pytaniu (zmierzone: „limity wpłat" vs ustawowy „próg zwolnienia").
    /// </summary>
    public int NeighbourhoodRadius { get; set; } = 2;

    /// <summary>Ile chunków z jednego aktu kwalifikuje go do rozszerzenia (ogranicza zasięg zmiany —
    /// pytania z rozproszonymi źródłami zachowują się jak dotąd).</summary>
    public int NeighbourhoodMinChunks { get; set; } = 3;

    /// <summary>Budżet tokenów na dociągnięte artykuły — cała obsługa przypadku „kodeks".</summary>
    public int NeighbourhoodTokenBudget { get; set; } = 20_000;

    /// <summary>Most vacatio legis: ile chunków dociągnąć z jednostek wskazanych w klauzuli wejścia
    /// w życie (0 = wyłączony). Patrz RetrievalQuery.VacatioLegisChunks.</summary>
    public int VacatioLegisChunks { get; set; } = 8;
}

/// <summary>Ugruntowanie odpowiedzi — bramki chroniące rdzeń wartości produktu.</summary>
public sealed class GroundingOptions
{
    /// <summary>
    /// Bramka anty-fabrykacji (Zadanie 10 planu ROU): odpowiedź powołująca się na artykuł/sygnaturę
    /// nieobecne w dostarczonym kontekście jest REGENEROWANA, a gdy druga próba też jest brudna —
    /// nie wychodzi. Domyślnie WŁĄCZONA: to poprawa bezpieczeństwa, a jej wyłączenie przywraca
    /// dzisiejsze zachowanie (badge ⚠ obok odpowiedzi, która i tak wychodzi).
    /// </summary>
    public bool CitationGateEnabled { get; set; } = true;
}

/// <summary>Przełączniki diagnostyczne (domyślnie wszystko wyłączone — zero śladu w UI/SSE).</summary>
public sealed class DiagnosticsOptions
{
    /// <summary>Pokazuj tokeny in/out przy każdej odpowiedzi (badge w UI + pole `usage` w SSE done).
    /// Włączenie: `dotnet run -- --Diagnostics:ShowTokenUsage=true` albo env Diagnostics__ShowTokenUsage.</summary>
    public bool ShowTokenUsage { get; set; }
}
