namespace PrawoRAG.Api.Services.Billing;

/// <summary>
/// Płatności Stripe (E3). <see cref="Enabled"/>=false (domyślnie) = trasy płatnicze nie są mapowane,
/// więc nic się nie zmienia — tak samo jak konta chowają się za <c>Auth:Enabled</c>.
///
/// Spike (US-3.1) świadomie idzie na JEDNYM sztucznym planie i jednej cenie: chodzi o poznanie stanów
/// subskrypcji, zanim powstanie cennik. Katalog planów zostaje tam, gdzie był — w <c>Plans:Items</c>.
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>true = mapujemy /platnosc/*; false = brak tras płatniczych (stan sprzed E3).</summary>
    public bool Enabled { get; set; }

    /// <summary>Klucz tajny Stripe (<c>sk_test_…</c> / <c>sk_live_…</c>). WYŁĄCZNIE ze zmiennych środowiskowych.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// Sekret podpisu webhooka (<c>whsec_…</c>). Bez niego endpoint webhooka nie ma jak odróżnić
    /// zdarzenia od Stripe od POST-a przysłanego przez kogokolwiek z internetu.
    /// </summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Identyfikator ceny w Stripe (<c>price_…</c>) odpowiadającej planowi płatnemu.</summary>
    public string PriceId { get; set; } = "";

    /// <summary>Plan nadawany po opłaceniu subskrypcji — klucz z <c>Plans:Items</c>.</summary>
    public string PaidPlanId { get; set; } = "pro";

    /// <summary>
    /// Ile dni po nieudanej płatności konto zachowuje dostęp (<c>past_due</c>). Odrzucona karta to
    /// nie to samo co rezygnacja — twarde odcięcie w tym stanie generuje wściekłe zgłoszenia od
    /// ludzi, którzy zapłacili.
    /// </summary>
    public int GraceDays { get; set; } = 7;
}
