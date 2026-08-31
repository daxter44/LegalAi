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
    private static string Shell(string title, bool analysisEnabled, string initials, string bodyHtml) => $$"""
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
          .topbar{display:flex;align-items:center;gap:var(--s-6);padding:var(--s-3) var(--s-10);
                  background:var(--sl-surface);border-bottom:1px solid var(--sl-border)}
          .brand{display:flex;align-items:center;gap:var(--s-2);text-decoration:none;color:inherit;
                 font-family:var(--sl-font-display);font-size:var(--fs-20);font-weight:700;letter-spacing:-0.01em}
          .brand .mark{width:22px;height:22px;border-radius:var(--sl-radius-md);background:var(--sl-gradient);display:inline-block}
          .nav{display:flex;gap:var(--s-5);font-size:var(--fs-14)}
          .nav a{color:var(--sl-text-secondary);text-decoration:none}
          .nav a:hover{color:var(--sl-accent)}
          .who{margin-left:auto;display:flex;align-items:center;gap:var(--s-3)}
          .who .active{font-size:var(--fs-14);font-weight:600;color:var(--sl-accent)}
          .avatar{width:30px;height:30px;border-radius:var(--sl-radius-full);background:var(--sl-bg-tertiary);
                  display:inline-flex;align-items:center;justify-content:center;
                  font-size:var(--fs-12);font-weight:700;color:var(--sl-text-secondary)}
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
          <a class="brand" href="/"><span class="mark"></span> {{E(AuthPages.ProductName)}}</a>
          <nav class="nav">
            <a href="/czat">Czat</a>
            {{(analysisEnabled ? """<a href="/analiza">Analiza</a>""" : "")}}
          </nav>
          <span class="who"><span class="active">Konto</span><span class="avatar">{{E(initials)}}</span></span>
        </div>
        <div class="content">
        {{bodyHtml}}
        </div>
        </body>
        </html>
        """;

    public static string Konto(string tokenField, string token, string planId, string planStatus,
        DateTime? validUntilUtc, bool hasSubscription, string? email, bool emailConfirmed,
        bool analysisEnabled)
    {
        var initials = string.IsNullOrWhiteSpace(email) ? "?" : char.ToUpperInvariant(email[0]).ToString();
        var planName = planId switch { "free" => "START", "pro" => "PRO", var x => x.ToUpperInvariant() };
        var statusPill = planStatus switch
        {
            "active" => """<span class="pill pill-ok">Aktywny</span>""",
            "canceled" => """<span class="pill pill-warn">Anulowany — działa do końca okresu</span>""",
            "past_due" => """<span class="pill pill-warn">Zaległa płatność</span>""",
            var s when !string.IsNullOrWhiteSpace(s) => $"""<span class="pill pill-warn">{E(s)}</span>""",
            _ => "",
        };

        return Shell("konto", analysisEnabled, initials, $"""
        <h1>Twoje konto</h1>

        <div class="card">
          <div class="row">
            <h2>Plan</h2>
            <span class="pill pill-plan">{E(planName)}</span>
            {statusPill}
            {(validUntilUtc is { } u ? $"""<span class="muted end" style="margin-left:auto">ważny do {u:yyyy-MM-dd HH:mm} UTC</span>""" : "")}
          </div>
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
          <div class="links">
            <a href="/prywatnosc">Polityka prywatności</a>
            <a href="/regulamin">Regulamin</a>
            <a class="btnlink btn-danger end" href="/wylogowanie">Wyloguj się</a>
          </div>
        </div>

        <div class="links"><a href="/czat">← Wróć do aplikacji</a></div>
        """);
    }
}
