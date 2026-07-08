namespace PrawoRAG.Api.Services;

/// <summary>
/// Bramka dostępu na zamknięty test (3.7): kody zaproszeń per tester + twarde dzienne limity kosztów LLM.
/// <see cref="Enabled"/>=false (domyślnie) = zachowanie jak dotąd (dev/M4 bez zmian); bramka włącza się
/// dopiero w deployu. Kody podawane przez env (Access__Invites__kod123=Jan) — nie commitowane.
/// Pełne Identity/OIDC (FE-5) świadomie poza zakresem — kilku testerów nie potrzebuje rejestracji.
/// </summary>
public sealed class AccessOptions
{
    public const string SectionName = "Access";

    /// <summary>false = wszystko otwarte jak dotąd; true = UI i API tylko dla zaproszonych.</summary>
    public bool Enabled { get; set; }

    /// <summary>Kod zaproszenia → nazwa testera. Kod per OSOBA (nie wspólny): nazwa staje się
    /// tożsamością (UserId) — rozmowy, feedback i limity są per tester.</summary>
    public Dictionary<string, string> Invites { get; set; } = [];

    /// <summary>Twardy cap zapytań LLM na dzień per tester.</summary>
    public int MaxUserRequestsPerDay { get; set; } = 50;

    /// <summary>Twardy cap zapytań LLM na dzień łącznie (wszyscy testerzy).</summary>
    public int MaxGlobalRequestsPerDay { get; set; } = 300;

    /// <summary>Twardy globalny dzienny budżet znaków WYJŚCIA LLM (proxy kosztu tokenów —
    /// providerzy streamują tekst bez liczników tokenów).</summary>
    public long MaxGlobalOutputCharsPerDay { get; set; } = 2_000_000;

    /// <summary>Rozpoznaje kod zaproszenia (trim, case-sensitive). Czysta — testowalna.</summary>
    public bool TryResolveInvite(string? code, out string testerName)
    {
        testerName = "";
        if (string.IsNullOrWhiteSpace(code)) return false;
        return Invites.TryGetValue(code.Trim(), out testerName!);
    }
}
