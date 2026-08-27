using System.Security.Claims;

namespace PrawoRAG.Api.Services;

/// <summary>Tożsamość bieżącego użytkownika — klucz, po którym izolowane są rozmowy, analizy i limity.</summary>
public interface ICurrentUser
{
    /// <summary>Stabilny identyfikator do zapisu w bazie. Nigdy nie pokazywać go w interfejsie.</summary>
    string UserId { get; }

    /// <summary>Nazwa do pokazania człowiekowi (e-mail albo imię i nazwisko). Może się zmieniać.</summary>
    string DisplayName { get; }

    bool IsAuthenticated { get; }
}

/// <summary>
/// Tożsamością jest identyfikator konta (claim <see cref="ClaimTypes.NameIdentifier"/>), a NIE e-mail
/// ani nazwa (E1/T-3). Powód: e-mail bywa zmieniany, a wtedy rozmowy i analizy zapisane pod starym
/// adresem przestałyby należeć do właściciela.
///
/// Kolejność źródeł jest istotna:
/// 1. identyfikator konta — konta Identity;
/// 2. nazwa z ciasteczka — stara bramka na kody zaproszeń (<c>Access:Enabled=true</c>), gdzie
///    tożsamością była nazwa testera; zachowane, żeby alfa działała bez zmian;
/// 3. <c>demo@local</c> — tylko gdy nic nie jest włączone (dev/M4).
///
/// Rozmowy z alfy mają w bazie nazwę testera, więc po przejściu na konta nie należą do nikogo —
/// świadome, opisane w planie E1.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private const string Placeholder = "demo@local";

    private ClaimsPrincipal? User => http.HttpContext?.User;

    public string UserId =>
        User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User?.Identity?.Name
        ?? Placeholder;

    public string DisplayName =>
        User?.FindFirstValue(ClaimTypes.Email)
        ?? User?.Identity?.Name
        ?? Placeholder;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;
}
