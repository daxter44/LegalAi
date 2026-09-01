using System.Globalization;
using Microsoft.Extensions.Options;
using PrawoRAG.Api.Services.Plans;

namespace PrawoRAG.Api.Services;

/// <summary>Werdykt bramki kosztów: czy wolno, a jeśli nie — co dokładnie się wyczerpało.
/// <paramref name="PlanLimit"/> = wyczerpał się LIMIT PLANU użytkownika (US-3.9: UI dokłada wtedy
/// link do konta/zakupu — to miejsce konwersji); limity pojemności/dzienne go nie ustawiają,
/// bo wyższy plan nic tam nie zmienia.</summary>
public readonly record struct CostDecision(bool Allowed, string? Message, bool PlanLimit = false)
{
    public static CostDecision Ok() => new(true, null);
    public static CostDecision Denied(string message, bool planLimit = false) => new(false, message, planLimit);
}

/// <summary>
/// Bramka kosztów (E1/T-10). Trzyma DWIE NIEZALEŻNE OSIE — mylenie ich to najczęstszy błąd w tego
/// typu kodzie:
///
/// • <b>Oś rozliczeniowa</b> — ile zapytań należy się KLIENTOWI: limit planu na okres rozliczeniowy
///   konta (15/mies. darmowy, 300/mies. płatny). W trybie bez kont (dev, bramka invite) jej miejsce
///   zajmuje dobowy limit per użytkownik z <see cref="AccessOptions"/> — zachowanie z alfy bez zmian.
/// • <b>Oś pojemnościowa</b> — ile zniesie NASZ SPRZĘT: globalne capy dobowe (zapytania i znaki
///   wyjścia). Zostaje niezależnie od tego, kto ile zapłacił; bez niej jeden klient z planem płatnym
///   potrafi położyć serwer w kilka godzin. Obowiązuje ZAWSZE, gdy cokolwiek jest liczone — także
///   w trybie kont z wyłączoną bramką invite (<c>Auth:Enabled=true</c>, <c>Access:Enabled=false</c>).
///   Do audytu 2026-09-01 (W3) była sprzężona z <c>Access:Enabled</c>, więc w trybie kont nie
///   działała: N darmowych kont = N×15 zapytań bez sufitu sprzętowego. Wartości capów nadal leżą
///   w <see cref="AccessOptions"/> (zgodność konfiguracji), ale <c>Access:Enabled</c> ich nie wyłącza.
///   Jedyny tryb bez liczenia: brak planu I brak bramki (dev/M4).
///
/// Liczniki są trwałe (wcześniej w pamięci procesu — restart zerował dzień). Zliczanie jest atomowe
/// po stronie magazynu (<see cref="IUsageCounters"/>), bo dwie karty przeglądarki tego samego
/// użytkownika nie mogą przepchnąć się ponad limit.
/// </summary>
public sealed class CostGuard(
    IUsageCounters counters,
    IOptions<AccessOptions> options,
    IEntitlements entitlements,
    TimeProvider time)
{
    private const string GlobalKey = "*";
    private static readonly CultureInfo Polish = new("pl-PL");

    /// <summary>
    /// Rezerwuje jedno zapytanie LLM. Kolejność jest celowa: najpierw to, co należy się klientowi
    /// (jego komunikat jest inny), potem pojemność systemu.
    /// </summary>
    public async Task<CostDecision> TryAcquireAsync(string userId, CancellationToken ct = default)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var entitlement = await entitlements.ForAsync(userId, ct);

        // Nic do liczenia: tryb bez kont i bez bramki (dev/M4) — zero zapytań do magazynu.
        if (!entitlement.PlanApplies && !o.Enabled) return CostDecision.Ok();

        // --- oś rozliczeniowa ---
        var reserved = false;
        if (entitlement is { PlanApplies: true, Limits: { } limits })
        {
            var used = await counters.TryIncrementAsync(UsageScopes.UserRequestsPeriod, userId,
                entitlement.Period.Key, limits.RequestsPerMonth, ct);
            if (used is null)
                return CostDecision.Denied(PlanLimitMessage(limits, entitlement.Period), planLimit: true);
            reserved = true;
        }
        else if (o.Enabled)
        {
            // Tryb bez kont: dobowy limit per tester (bramka invite, alfa) — bit w bit jak dotąd.
            var used = await counters.TryIncrementAsync(UsageScopes.UserRequestsDay, userId, today,
                o.MaxUserRequestsPerDay, ct);
            if (used is null)
                return CostDecision.Denied(Limit("Twój dzienny limit zapytań"));
            reserved = true;
        }

        // --- oś pojemnościowa: budżet znaków (dolicza się PO odpowiedzi, w RecordAsync) ---
        // Bez warunku na o.Enabled: skoro doszliśmy tutaj, coś jest liczone (plan albo bramka),
        // więc pojemność też ma być pilnowana (W3).
        var chars = await counters.CurrentAsync(UsageScopes.GlobalCharsDay, GlobalKey, today, ct);
        if (chars >= o.MaxGlobalOutputCharsPerDay)
            return await RefundAndDenyAsync(entitlement, userId, today, reserved,
                Limit("globalny dzienny budżet odpowiedzi"), ct);

        // --- oś pojemnościowa: zapytania na dobę ---
        var global = await counters.TryIncrementAsync(UsageScopes.GlobalRequestsDay, GlobalKey, today,
            o.MaxGlobalRequestsPerDay, ct);
        if (global is null)
            return await RefundAndDenyAsync(entitlement, userId, today, reserved,
                Limit("globalny dzienny limit zapytań"), ct);

        return CostDecision.Ok();
    }

    /// <summary>Dolicza rozmiar odpowiedzi LLM do dobowego budżetu znaków (po zakończeniu streamu).</summary>
    public async Task RecordAsync(string userId, int outputChars, CancellationToken ct = default)
    {
        if (outputChars <= 0) return;
        // Lustro TryAcquireAsync: liczymy zawsze, gdy obowiązuje plan LUB bramka; tylko dev/M4
        // (ani jedno, ani drugie) nie dotyka magazynu.
        if (!options.Value.Enabled && !(await entitlements.ForAsync(userId, ct)).PlanApplies) return;
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        await counters.AddAsync(UsageScopes.GlobalCharsDay, GlobalKey, today, outputChars, ct);
    }

    /// <summary>Zużycie w bieżącym okresie (ekran planu, komunikaty). <c>null</c> = plan nie obowiązuje.</summary>
    public async Task<(int Used, int Limit)?> UsageAsync(string userId, CancellationToken ct = default)
    {
        var entitlement = await entitlements.ForAsync(userId, ct);
        if (entitlement.Limits is not { } limits) return null;

        var used = await counters.CurrentAsync(UsageScopes.UserRequestsPeriod, userId,
            entitlement.Period.Key, ct);
        return ((int)Math.Min(used, int.MaxValue), limits.RequestsPerMonth);
    }

    /// <summary>
    /// Komunikat wyczerpanego limitu planu — MIEJSCE KONWERSJI (T-11), nie komunikat błędu. Mówi, co
    /// się skończyło, kiedy się odnawia i (dla darmowego) że istnieje wyższy plan.
    /// </summary>
    private static string PlanLimitMessage(PlanLimits limits, BillingPeriod period)
    {
        var text = $"Wykorzystano limit planu {limits.DisplayName} " +
                   $"({limits.RequestsPerMonth} zapytań na okres rozliczeniowy). " +
                   $"Odnowi się {period.EndUtc.ToString("d MMMM yyyy", Polish)}.";
        return limits.RequestsPerMonth < ProPlanRequests
            ? text + $" Plan Pro daje {ProPlanRequests} zapytań miesięcznie."
            : text;
    }

    /// <summary>E3 podmieni to na odczyt z katalogu planów wraz z cennikiem i odnośnikiem do zakupu.</summary>
    private const int ProPlanRequests = 300;

    private static string Limit(string reason) =>
        $"Wyczerpany {reason} — spróbuj ponownie jutro.";

    /// <summary>
    /// Zwrot rezerwacji, gdy zapytanie odbił dopiero cap pojemności. Bez tego klient traciłby
    /// zapytanie z pakietu za ograniczenie po NASZEJ stronie.
    /// </summary>
    private async Task<CostDecision> RefundAndDenyAsync(
        Entitlement entitlement, string userId, DateOnly today, bool reserved, string message, CancellationToken ct)
    {
        if (reserved)
        {
            var (scope, period) = entitlement.PlanApplies
                ? (UsageScopes.UserRequestsPeriod, entitlement.Period.Key)
                : (UsageScopes.UserRequestsDay, today);
            await counters.AddAsync(scope, userId, period, -1, ct);
        }
        return CostDecision.Denied(message);
    }
}
