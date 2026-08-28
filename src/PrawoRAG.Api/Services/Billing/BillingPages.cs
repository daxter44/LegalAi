using System.Text.Encodings.Web;
using PrawoRAG.Api.Services.Auth;

namespace PrawoRAG.Api.Services.Billing;

/// <summary>
/// Strona konta/planu (E3/US-3.1 — spike) — sama tylko prezentacja i dwa formularze POST-em
/// (<c>/platnosc/start</c>, <c>/platnosc/portal</c>). Zero logiki nadającej uprawnienia: to robi
/// wyłącznie webhook, patrz komentarz w <see cref="BillingEndpoints"/>.
/// </summary>
public static class BillingPages
{
    private static string E(string? v) => HtmlEncoder.Default.Encode(v ?? "");

    public static string Konto(string tokenField, string token, string planId, string planStatus,
        DateTime? validUntilUtc, bool hasSubscription) => AuthPages.Page("konto", $"""
        <h1>Twoje konto</h1>
        <p>Plan: <strong>{E(planId)}</strong> ({E(planStatus)}){
            (validUntilUtc is { } u ? $" — ważny do {u:yyyy-MM-dd HH:mm} UTC" : "")}</p>

        <form method="post" action="/platnosc/start">
          {AuthPages.Token(tokenField, token)}
          <button type="submit">{(hasSubscription ? "Zmień plan" : "Wykup plan")}</button>
        </form>

        {(hasSubscription ? $$"""
        <form method="post" action="/platnosc/portal" style="margin-top:var(--s-3)">
          {{AuthPages.Token(tokenField, token)}}
          <button type="submit">Zarządzaj płatnościami</button>
        </form>
        """ : "")}

        <div class="links"><a href="/czat">Wróć do aplikacji</a></div>
        """);
}
