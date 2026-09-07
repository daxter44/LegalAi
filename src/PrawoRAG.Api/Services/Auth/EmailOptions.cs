namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Wysyłka poczty transakcyjnej (potwierdzenie adresu, reset hasła). Provider "resend" = realna wysyłka
/// przez API Resend; "log" (domyślnie) = zapis treści do logu, żeby dev działał bez klucza i bez
/// wysyłania czegokolwiek na świat.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>"resend" albo "log". Domyślnie "log" — bezpieczny default dla dev.</summary>
    public string Provider { get; set; } = "log";

    /// <summary>Klucz API Resend. WYŁĄCZNIE ze zmiennej środowiskowej / sekretów, nigdy w repo.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Nadawca, np. "PrawoRAG &lt;noreply@przyklad.pl&gt;". Domena musi być zweryfikowana w Resend.</summary>
    public string From { get; set; } = "";

    /// <summary>Opcjonalny adres do odpowiedzi (kontakt z zespołem).</summary>
    public string ReplyTo { get; set; } = "";
}
