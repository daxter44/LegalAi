using System.Security.Claims;

namespace PrawoRAG.Api.Services;

/// <summary>
/// JEDNO miejsce, które mówi, co jest kluczem użytkownika w bazie i w licznikach. Używają go
/// <see cref="CurrentUser"/> (ścieżka HTTP), endpointy <c>/api/*</c> (ResolveApiUser w Program.cs)
/// i komponenty Blazora (Chat/Analiza/Szukaj czytają tożsamość z AuthenticationState, bo
/// IHttpContextAccessor bywa null w obwodzie).
///
/// Dlaczego to istnieje (audyt OWASP LLM 2026-09-01, ustalenie W2): komponenty brały
/// <c>Identity.Name</c> (= UserName = e-mail), a reszta systemu <see cref="ClaimTypes.NameIdentifier"/>
/// (= Id konta). Skutek w trybie kont: rozmowy i analizy z UI lądowały pod e-mailem, a plan
/// i limity szukały konta po Id — czyli limit planu w UI nie działał, a ten sam człowiek miał dwie
/// rozłączne historie (UI vs API). Dopóki każdy woła tę metodę, taki rozjazd nie może wrócić.
/// </summary>
public static class UserIdentity
{
    /// <summary>
    /// Stabilny klucz tożsamości albo <c>null</c>, gdy principal nie niesie żadnego.
    /// Kolejność: identyfikator konta (Identity) → nazwa (stara bramka na kody zaproszeń, gdzie
    /// tożsamością była nazwa testera). Nigdy e-mail: ten bywa zmieniany, a rozmowy zapisane pod
    /// starym adresem przestałyby należeć do właściciela.
    /// </summary>
    public static string? KeyOf(ClaimsPrincipal? user)
    {
        if (user is null) return null;
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(id)) return id;
        var name = user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
