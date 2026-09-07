namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Licznik zużycia w bazie (E1/T-10). Zastępuje liczniki w pamięci procesu, które zerowały się przy
/// każdym restarcie — przy planie, za który ktoś zapłacił, to przestało być akceptowalne.
///
/// Klucz złożony (<see cref="Scope"/>, <see cref="Key"/>, <see cref="PeriodStart"/>) robi całą robotę:
/// zmiana okresu daje inny wiersz, więc licznik startuje od zera BEZ żadnego zadania czyszczącego
/// w tle. Stare wiersze zostają jako historia zużycia.
/// </summary>
public sealed class UsageCounterEntity
{
    /// <summary>Co liczymy — wartości w <c>UsageScopes</c> po stronie API.</summary>
    public string Scope { get; set; } = "";

    /// <summary>Czyje: identyfikator konta albo <c>*</c> dla liczników globalnych.</summary>
    public string Key { get; set; } = "";

    /// <summary>Początek okresu: miesięcznego dla planu, dobowego dla capów pojemności.</summary>
    public DateOnly PeriodStart { get; set; }

    public long Value { get; set; }
}
