namespace PrawoRAG.Api.Services.Plans;

/// <summary>Okres rozliczeniowy konta: od kiedy, do kiedy i klucz do licznika w bazie.</summary>
public readonly record struct BillingPeriod(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>
    /// Klucz licznika — data początku okresu. Zmiana okresu = inny klucz = licznik startuje od zera,
    /// bez żadnego zadania czyszczącego w tle.
    /// </summary>
    public DateOnly Key => DateOnly.FromDateTime(StartUtc);
}

/// <summary>
/// Wyznacza bieżący okres rozliczeniowy konta (E1/T-8).
///
/// Okres biegnie od DNIA MIESIĄCA, w którym konto powstało (a po wdrożeniu E3 — w którym ruszyła
/// subskrypcja), nie od pierwszego dnia kalendarzowego. Powód jest praktyczny: Stripe rozlicza
/// w okresach liczonych od dnia zakupu, więc kalendarzowy miesiąc trzeba by przy płatnościach
/// przepisać. Tutaj to kilka linii, tam byłaby zmiana semantyki na żywych licznikach.
///
/// Dni brzegowe: konto z kotwicą 31. w lutym dostaje ostatni dzień lutego (i wraca na 31. w marcu) —
/// standardowe zachowanie „tego samego dnia miesiąca", bez gubienia okresów.
/// </summary>
public static class BillingPeriodCalculator
{
    public static BillingPeriod Current(DateTime anchorUtc, DateTime nowUtc)
    {
        var anchorDay = anchorUtc.Day;

        // Start szukamy w bieżącym miesiącu „teraz"; jeśli jeszcze nie minął dzień kotwicy,
        // bieżący okres zaczął się w miesiącu poprzednim.
        var start = OnDay(nowUtc.Year, nowUtc.Month, anchorDay, anchorUtc);
        if (start > nowUtc)
        {
            var prev = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            start = OnDay(prev.Year, prev.Month, anchorDay, anchorUtc);
        }

        // Konto młodsze niż jeden okres: liczymy od założenia, nie od daty sprzed jego istnienia.
        if (start < anchorUtc) start = anchorUtc;

        var afterStart = start.AddDays(1);
        var end = OnDay(afterStart.Year, afterStart.Month, anchorDay, anchorUtc);
        if (end <= start) end = OnDay(afterStart.AddMonths(1).Year, afterStart.AddMonths(1).Month, anchorDay, anchorUtc);

        return new BillingPeriod(start, end);
    }

    /// <summary>Ten sam dzień miesiąca co kotwica, przycięty do długości miesiąca (31 → 28/29/30).</summary>
    private static DateTime OnDay(int year, int month, int day, DateTime anchorUtc)
    {
        var safeDay = Math.Min(day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, safeDay, anchorUtc.Hour, anchorUtc.Minute, anchorUtc.Second, DateTimeKind.Utc);
    }
}
