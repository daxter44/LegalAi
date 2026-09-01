using System.Globalization;
using System.Text.Encodings.Web;
using PrawoRAG.Api.Services.Auth;

namespace PrawoRAG.Api.Services.Billing;

/// <summary>
/// Strona konta/planu (E3/US-3.1 + RED-4.2) — sama tylko prezentacja i dwa formularze POST-em
/// (<c>/platnosc/start</c>, <c>/platnosc/portal</c>). Zero logiki nadającej uprawnienia: to robi
/// wyłącznie webhook, patrz komentarz w <see cref="BillingEndpoints"/>. Układ wg makiety Konto
/// (topbar aplikacji + karty: Plan, Dane konta, Prywatność). „Hasło" prowadzi do ISTNIEJĄCEGO
/// przepływu resetu (/haslo/reset) — osobnej zmiany hasła w produkcie nie ma i strona jej nie udaje.
/// </summary>
public static class BillingPages
{
    private static string E(string? v) => HtmlEncoder.Default.Encode(v ?? "");

    /// <summary>Oprawa strony konta: topbar z nawigacją aplikacji + wyśrodkowana kolumna kart.
    /// `$$"""` bo CSS w środku (klamry); wartości wstrzykiwane wyłącznie przez E().</summary>
    private static string Shell(string title, bool analysisEnabled, string initials, string whoName,
        string bodyHtml) => $$"""
        <!doctype html>
        <html lang="pl">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{E(AuthPages.ProductName)}} — {{E(title)}}</title>
        <link rel="stylesheet" href="/css/tokens.css">
        <style>
          body{font-family:var(--sl-font-base);background:var(--sl-bg);color:var(--sl-text-primary);margin:0;line-height:var(--lh-body)}
          /* Topbar 1:1 z globalnym headerem aplikacji (app.css .app-header) — spójność 2026-09-01. */
          .topbar{display:flex;align-items:center;gap:var(--s-6);min-height:60px;padding:0 var(--s-8);
                  background:#171B24;border-bottom:1px solid rgb(199 208 236 / .12)}
          .brand{display:flex;align-items:center;gap:10px;text-decoration:none;color:var(--sl-on-dark);
                 font-family:var(--sl-font-display);font-size:var(--fs-20);font-weight:700;letter-spacing:-0.01em}
          .brand .mark{width:26px;height:26px;border-radius:var(--sl-radius-md);background:var(--sl-gradient);
                 box-shadow:0 0 24px rgb(37 99 235 / .6);display:inline-block}
          .nav{margin-left:auto;display:flex;align-items:center;gap:var(--s-5);font-size:var(--fs-15)}
          .nav a{color:#9BA3B7;font-weight:500;text-decoration:none;white-space:nowrap}
          .nav a:hover{color:var(--sl-on-dark)}
          .topbar a:focus-visible{outline:2px solid var(--sl-on-dark-accent);outline-offset:2px}
          .who{display:flex;align-items:center;gap:var(--s-3)}
          .who .active{font-size:var(--fs-15);font-weight:600;color:var(--sl-on-dark)}
          .avatar{width:30px;height:30px;border-radius:var(--sl-radius-full);background:rgb(199 208 236 / .15);
                  display:inline-flex;align-items:center;justify-content:center;
                  font-size:var(--fs-12);font-weight:700;color:var(--sl-on-dark-soft)}
          /* Hamburger jak w headerze aplikacji (app.css .nav-burger) — <details> bez JS. */
          .nav-burger{display:none;position:relative}
          .nav-burger>summary{list-style:none;cursor:pointer;user-select:none;color:#9BA3B7;
                 font-size:var(--fs-20);line-height:1;padding:var(--s-2) var(--s-3);border-radius:var(--sl-radius-md)}
          .nav-burger>summary::-webkit-details-marker{display:none}
          .nav-burger[open]>summary{color:var(--sl-on-dark);background:rgb(199 208 236 / .12)}
          .nav-sheet{position:absolute;right:0;top:calc(100% + 8px);z-index:200;min-width:220px;
                 display:flex;flex-direction:column;gap:var(--s-1);padding:var(--s-2);
                 background:#171B24;border:1px solid rgb(199 208 236 / .18);
                 border-radius:var(--sl-radius-lg);box-shadow:var(--sl-shadow-lg)}
          .nav-sheet a{color:#9BA3B7;text-decoration:none;font-size:var(--fs-15);font-weight:500;
                 padding:var(--s-2) var(--s-3);border-radius:var(--sl-radius-md);white-space:nowrap}
          .nav-sheet a:hover{color:var(--sl-on-dark);background:rgb(199 208 236 / .1)}
          @media(max-width:720px){
            .topbar{gap:var(--s-4);padding:0 var(--s-4)}
            .topbar .nav{display:none}
            .who{margin-left:auto}
            .nav-burger{display:block}
          }
          .content{max-width:48rem;margin:0 auto;padding:var(--s-10) var(--s-4);display:flex;flex-direction:column;gap:var(--s-5)}
          h1{font-family:var(--sl-font-display);font-size:var(--fs-30);letter-spacing:-0.01em;margin:0}
          .card{background:var(--sl-surface);border-radius:var(--sl-radius-xl);box-shadow:var(--sl-shadow-card);
                padding:var(--s-6) var(--s-8);display:flex;flex-direction:column;gap:var(--s-4)}
          .card h2{font-size:var(--fs-16);margin:0}
          .row{display:flex;align-items:center;gap:var(--s-3);flex-wrap:wrap}
          .pill{display:inline-flex;align-items:center;padding:3px 12px;border-radius:var(--sl-radius-full);
                font-size:var(--fs-12);font-weight:700;letter-spacing:.03em}
          .pill-plan{background:var(--sl-gradient);color:var(--sl-text-inverse)}
          .pill-ok{background:var(--sl-success-bg);color:var(--sl-success)}
          .pill-warn{background:var(--sl-warning-bg);color:var(--sl-warning)}
          .muted{color:var(--sl-text-tertiary);font-size:var(--fs-13)}
          .usage{display:flex;flex-direction:column;gap:var(--s-2)}
          .usage-head{display:flex;align-items:baseline;gap:var(--s-2);font-size:var(--fs-14);color:var(--sl-text-secondary)}
          .usage-head .num{margin-left:auto;font-weight:700;color:var(--sl-text-primary);font-variant-numeric:tabular-nums}
          .usage-head .den{color:var(--sl-text-tertiary);font-weight:500}
          .meter{height:10px;border-radius:var(--sl-radius-full);background:var(--sl-bg-secondary);overflow:hidden}
          .meter-fill{height:10px;border-radius:var(--sl-radius-full);background:var(--sl-gradient)}
          .kv{display:flex;align-items:center;gap:var(--s-3);padding:var(--s-3) 0;border-bottom:1px solid var(--sl-border)}
          .kv:last-child{border-bottom:0}
          .kv .k{font-size:var(--fs-13);color:var(--sl-text-tertiary)}
          .kv .v{font-size:var(--fs-15);font-weight:600}
          .kv .end{margin-left:auto}
          .btns{display:flex;gap:var(--s-3);flex-wrap:wrap}
          form{margin:0}
          button,.btnlink{font-family:inherit;font-size:var(--fs-15);font-weight:700;min-height:44px;
                 padding:0 var(--s-5);border-radius:var(--sl-radius-lg);border:0;cursor:pointer;
                 display:inline-flex;align-items:center;text-decoration:none}
          .btn-grad{background:var(--sl-gradient);color:var(--sl-text-inverse);box-shadow:var(--sl-shadow-accent)}
          .btn-grad:hover{filter:brightness(1.07)}
          .btn-line{background:var(--sl-surface);color:var(--sl-text-primary);border:1px solid var(--sl-border);font-weight:600}
          .btn-line:hover{border-color:var(--sl-accent);color:var(--sl-accent)}
          .btn-danger{background:var(--sl-surface);color:var(--sl-error);border:1px solid var(--sl-error-border);font-weight:600}
          .info{display:flex;gap:var(--s-2);padding:var(--s-3) var(--s-4);background:var(--sl-accent-light);
                border-radius:var(--sl-radius-lg);font-size:var(--fs-14);color:var(--sl-text-secondary);line-height:1.6}
          a{color:var(--sl-accent)}
          .links{display:flex;gap:var(--s-4);align-items:center;font-size:var(--fs-14)}
          .links .end{margin-left:auto}
        </style>
        </head>
        <body>
        <div class="topbar">
          <a class="brand" href="/start"><span class="mark"></span> {{E(AuthPages.ProductName)}}</a>
          <nav class="nav">
            <a href="/czat">Czat</a>
            {{(analysisEnabled ? """<a href="/analiza">Analiza</a>""" : "")}}
            <a href="/o-systemie">O systemie</a>
          </nav>
          <span class="who"><span class="avatar">{{E(initials)}}</span><span class="active">{{E(whoName)}}</span></span>
          <details class="nav-burger">
            <summary aria-label="Menu">☰</summary>
            <nav class="nav-sheet">
              <a href="/czat">Czat</a>
              {{(analysisEnabled ? """<a href="/analiza">Analiza</a>""" : "")}}
              <a href="/o-systemie">O systemie</a>
            </nav>
          </details>
        </div>
        <div class="content">
        {{bodyHtml}}
        </div>
        {{AnalyticsSnippet.Html}}
        </body>
        </html>
        """;

    public static string Konto(string tokenField, string token, string planId, string planStatus,
        DateTime? validUntilUtc, bool hasSubscription, string? email, bool emailConfirmed,
        bool analysisEnabled, (int Used, int Limit)? usage = null, DateTime? periodEndUtc = null,
        string? displayName = null, DateTime? marketingConsentAtUtc = null)
    {
        // Inicjał i podpis jak w chipie konta nagłówka aplikacji: imię z rejestracji, e-mail w odwodzie.
        var whoSource = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
        var initials = string.IsNullOrWhiteSpace(whoSource) ? "?" : char.ToUpperInvariant(whoSource[0]).ToString();
        var whoName = string.IsNullOrWhiteSpace(displayName)
            ? "Konto"
            : displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var planName = planId switch { "free" => "START", "pro" => "PRO", var x => x.ToUpperInvariant() };
        var statusPill = planStatus switch
        {
            "active" => """<span class="pill pill-ok">Aktywny</span>""",
            "canceled" => """<span class="pill pill-warn">Anulowany — działa do końca okresu</span>""",
            "past_due" => """<span class="pill pill-warn">Zaległa płatność</span>""",
            var s when !string.IsNullOrWhiteSpace(s) => $"""<span class="pill pill-warn">{E(s)}</span>""",
            _ => "",
        };

        // Pasek zużycia X/Y (makieta Konto: „Zużycie w tym okresie") — tylko gdy plan obowiązuje.
        var usageHtml = "";
        if (usage is { } u && u.Limit > 0)
        {
            var pct = Math.Min(100, (int)Math.Round(100.0 * u.Used / u.Limit));
            var renews = periodEndUtc is { } end
                ? $"Licznik odnowi się {end.ToString("d MMMM yyyy", new CultureInfo("pl-PL"))}."
                : "Licznik odnawia się z początkiem każdego okresu rozliczeniowego.";
            usageHtml = $"""
          <div class="usage">
            <div class="usage-head"><span>Zużycie w tym okresie</span>
              <span class="num">{u.Used} <span class="den">/ {u.Limit} zapytań</span></span></div>
            <div class="meter" role="img" aria-label="Zużyto {u.Used} z {u.Limit} zapytań"><div class="meter-fill" style="width:{pct}%"></div></div>
            <span class="muted">{E(renews)}</span>
          </div>
          """;
        }

        return Shell("konto", analysisEnabled, initials, whoName, $"""
        <h1>Twoje konto</h1>

        <div class="card">
          <div class="row">
            <h2>Plan</h2>
            <span class="pill pill-plan">{E(planName)}</span>
            {statusPill}
            {(validUntilUtc is { } vu ? $"""<span class="muted end" style="margin-left:auto">ważny do {vu:yyyy-MM-dd HH:mm} UTC</span>""" : "")}
          </div>
          {usageHtml}
          <div class="btns">
            <form method="post" action="/platnosc/start">
              {AuthPages.Token(tokenField, token)}
              <button type="submit" class="btn-grad">{(hasSubscription ? "Zmień plan" : "Wykup plan")}</button>
            </form>
            {(hasSubscription ? $"""
            <form method="post" action="/platnosc/portal">
              {AuthPages.Token(tokenField, token)}
              <button type="submit" class="btn-line">Zarządzaj płatnościami</button>
            </form>
            """ : "")}
          </div>
          <span class="muted">Płatnościami i fakturami zarządzasz w bezpiecznym panelu Stripe — nie przechowujemy danych karty.</span>
        </div>

        <div class="card">
          <h2>Dane konta</h2>
          <div class="kv">
            <span><span class="k">Imię i nazwisko</span><br><span class="v">{(string.IsNullOrWhiteSpace(displayName) ? """<span class="muted">nie podano</span>""" : E(displayName))}</span></span>
          </div>
          <div class="kv">
            <span><span class="k">Adres e-mail</span><br><span class="v">{E(email)}</span></span>
            <span class="end">{(emailConfirmed ? """<span class="pill pill-ok">potwierdzony</span>""" : """<span class="pill pill-warn">niepotwierdzony</span>""")}</span>
          </div>
          <div class="kv">
            <span><span class="k">Hasło</span><br><span class="v">••••••••••</span></span>
            <a class="btnlink btn-line end" href="/haslo/reset">Zmień hasło (przez e-mail)</a>
          </div>
        </div>

        <div class="card">
          <h2>Prywatność i dane</h2>
          <div class="info">Twoje pytania i dokumenty nie trenują żadnego modelu. Konto możesz usunąć na żądanie — tryb opisuje polityka prywatności.</div>
          <div class="kv">
            <span><span class="k">Zgoda na treści marketingowe</span><br>
              <span class="v">{(marketingConsentAtUtc is { } mc
                  ? $"""wyrażona {mc:yyyy-MM-dd}"""
                  : """<span class="muted">brak</span>""")}</span></span>
            <form method="post" action="/konto/zgoda-marketingowa" class="end">
              {AuthPages.Token(tokenField, token)}
              <input type="hidden" name="zgoda" value="{(marketingConsentAtUtc is null ? "tak" : "nie")}">
              <button type="submit" class="btn-line">{(marketingConsentAtUtc is null ? "Wyraź zgodę" : "Wycofaj zgodę")}</button>
            </form>
          </div>
          <div class="links">
            <a href="/prywatnosc">Polityka prywatności</a>
            <a href="/regulamin">Regulamin</a>
            <a href="#" id="cookie-settings">Ustawienia cookies</a>
            <a class="btnlink btn-danger end" href="/wylogowanie">Wyloguj się</a>
          </div>
        </div>

        <div class="links"><a href="/czat">← Wróć do aplikacji</a></div>
        """);
    }
}
