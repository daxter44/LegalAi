using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Access;

/// <summary>
/// Bramka dostępu 3.7 (czyste, bez HTTP): rozpoznawanie kodów zaproszeń. Limity kosztów wyprowadzone
/// do CostGuardRulesTests (reguły dwóch osi) i CostGuardLiveTests (magazyn liczników) — E1/T-10
/// rozdzielił oś planu od osi pojemności, więc jeden plik testów przestał wystarczać.
/// </summary>
public class AccessGateTests
{
    // --- AccessOptions.TryResolveInvite ---

    [Fact]
    public void Invite_resolves_with_trim()
    {
        var o = new AccessOptions { Invites = { ["kod123"] = "Jan Kowalski" } };

        Assert.True(o.TryResolveInvite("  kod123 ", out var name));
        Assert.Equal("Jan Kowalski", name);
    }

    [Fact]
    public void Invalid_or_empty_code_is_rejected()
    {
        var o = new AccessOptions { Invites = { ["kod123"] = "Jan" } };

        Assert.False(o.TryResolveInvite("zly-kod", out _));
        Assert.False(o.TryResolveInvite("", out _));
        Assert.False(o.TryResolveInvite(null, out _));
        Assert.False(o.TryResolveInvite("KOD123", out _)); // case-sensitive
    }


    // CostGuard ma teraz własne testy: reguły dwóch osi w CostGuardRulesTests (bez bazy),
    // trwałość i atomowość liczników w CostGuardLiveTests (żywy Postgres).
}
