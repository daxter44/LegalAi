using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Dokłada do ciasteczka claimy, których Identity samo nie wystawia, a interfejs potrzebuje na
/// każdej stronie: imię i nazwisko z rejestracji (GivenName) oraz e-mail. Bez tego nagłówek
/// pokazywał goły adres e-mail mimo podanego imienia — <see cref="ICurrentUser"/> nie ma skąd wziąć
/// imienia, bo w ścieżce zapytania nie sięgamy do bazy. Zmiana danych wejdzie przy kolejnym
/// logowaniu (świadomie: konto nie ma dziś edycji imienia).
/// </summary>
public sealed class AppClaimsFactory(
    UserManager<AppUserEntity> users,
    IOptions<IdentityOptions> options) : UserClaimsPrincipalFactory<AppUserEntity>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUserEntity user)
    {
        var id = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            id.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        if (!string.IsNullOrWhiteSpace(user.Email))
            id.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        return id;
    }
}
