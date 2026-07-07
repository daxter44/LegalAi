using System.Security.Claims;

namespace PrawoRAG.Api.Services;

/// <summary>Tożsamość bieżącego użytkownika — hak pod user scope. Przed FE-5 (auth) zwraca placeholder.</summary>
public interface ICurrentUser
{
    string UserId { get; }
    bool IsAuthenticated { get; }
}

/// <summary>
/// Odczytuje tożsamość z claimów OIDC (e-mail → nazwa). Zanim wejdzie logowanie (FE-5) zwraca „demo@local",
/// żeby persystencja działała już teraz. Po FE-5 ten sam interfejs poda realny e-mail zalogowanego.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private const string Placeholder = "demo@local";

    public string UserId =>
        http.HttpContext?.User?.FindFirstValue(ClaimTypes.Email)
        ?? http.HttpContext?.User?.Identity?.Name
        ?? Placeholder;

    public bool IsAuthenticated => http.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
}
