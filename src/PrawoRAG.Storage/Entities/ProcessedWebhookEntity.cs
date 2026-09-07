namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Zdarzenie webhooka, które już przetworzyliśmy (E3/US-3.5). Stripe dostarcza zdarzenia
/// <b>co najmniej raz</b> i nie zawsze w kolejności, więc bez tej tabeli:
/// • ponowione zdarzenie zakupu nadałoby plan drugi raz (albo przesunęło okres),
/// • spóźnione „anulowano" wyłączyłoby konto, które właśnie się przedłużyło.
///
/// Klucz główny to identyfikator zdarzenia ze Stripe (<c>evt_…</c>) — wstawienie duplikatu wywala się
/// na kluczu i to jest cała ochrona, bez żadnego dodatkowego zamka.
/// </summary>
public sealed class ProcessedWebhookEntity
{
    /// <summary>Identyfikator zdarzenia u dostawcy (<c>evt_…</c>).</summary>
    public string EventId { get; set; } = "";

    /// <summary>Typ zdarzenia — trzymany do diagnozy, nie do decyzji.</summary>
    public string EventType { get; set; } = "";

    /// <summary>Kiedy je przyjęliśmy (UTC).</summary>
    public DateTime ProcessedAtUtc { get; set; }
}
