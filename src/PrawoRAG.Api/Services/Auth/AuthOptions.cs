namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Konta użytkowników (E1, blok A). <see cref="Enabled"/>=false zostawia zachowanie sprzed kont
/// (dev/M4, bramka invite) — konta włącza się świadomie, tak jak swojego czasu bramkę.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>true = rejestracja/logowanie kontem; false = jak dotąd (kod zaproszenia albo otwarte dev).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Publiczny adres bazowy do linków w e-mailach (np. https://przyklad.pl). Pusty = adres brany
    /// z bieżącego żądania. W produkcji USTAWIĆ: link z e-maila wysyłanego zza proxy nie może
    /// zależeć od nagłówków, którymi steruje klient.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Wersja regulaminu akceptowana przy rejestracji (zapisywana na koncie).</summary>
    public string TermsVersion { get; set; } = "2026-08";

    /// <summary>Wersja treści zgody marketingowej — zapisywana przy jej wyrażeniu (dowód RODO).</summary>
    public string MarketingConsentVersion { get; set; } = "2026-09";
}
