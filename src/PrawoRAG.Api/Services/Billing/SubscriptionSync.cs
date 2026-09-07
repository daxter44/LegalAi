using PrawoRAG.Api.Services.Plans;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services.Billing;

/// <summary>
/// Stan subskrypcji sprowadzony do tego, co nas interesuje — bez typów Stripe, żeby regułę dało się
/// testować bez sieci i bez biblioteki dostawcy. Mapowanie ze Stripe robi endpoint webhooka.
/// </summary>
/// <param name="Status">Surowy status Stripe: active, trialing, past_due, unpaid, canceled, incomplete…</param>
/// <param name="EventTimeUtc">Czas ZDARZENIA (nie odbioru) — po nim poznajemy zdarzenia spóźnione.</param>
public readonly record struct SubscriptionState(
    string CustomerId,
    string SubscriptionId,
    string Status,
    DateTime? CurrentPeriodStartUtc,
    DateTime? CurrentPeriodEndUtc,
    bool CancelAtPeriodEnd,
    DateTime EventTimeUtc,
    bool Deleted = false);

/// <summary>
/// Przełożenie stanu subskrypcji na uprawnienie konta (E3). Cała reguła jest tutaj, w jednym miejscu
/// i bez wejścia do sieci — bo to ona decyduje, czy płacący klient ma dostęp.
///
/// Trzy rzeczy, które w tego typu kodzie psują się najczęściej i dlatego mają tu jawną obsługę:
///
/// 1. <b>Zdarzenia spóźnione i nie po kolei.</b> Stripe nie gwarantuje kolejności, więc porównujemy
///    czas zdarzenia z <see cref="AppUserEntity.PlanUpdatedAtUtc"/> i starsze ignorujemy. Bez tego
///    spóźnione „anulowano" wyłącza konto, które właśnie się przedłużyło.
/// 2. <b><c>past_due</c> to nie rezygnacja.</b> Odrzucona karta daje okres łaski
///    (<see cref="BillingOptions.GraceDays"/>), a nie natychmiastowe odcięcie — klient zapłacił,
///    a Stripe i tak ponawia obciążenie przez kilka dni.
/// 3. <b>Anulowanie działa do końca opłaconego okresu.</b> Rezygnacja w połowie miesiąca nie zabiera
///    dostępu od razu; dopiero wygaśnięcie okresu (albo zdarzenie usunięcia subskrypcji) degraduje
///    konto do planu darmowego.
/// </summary>
public static class SubscriptionSync
{
    /// <summary>
    /// Nakłada stan subskrypcji na konto. Zwraca false, gdy zdarzenie jest starsze niż ostatnio
    /// zastosowane (nic nie zmieniamy).
    /// </summary>
    public static bool Apply(AppUserEntity user, SubscriptionState state, BillingOptions options, DateTime nowUtc)
    {
        // Zdarzenie starsze niż stan konta = echo z przeszłości. Ignorujemy.
        if (user.PlanUpdatedAtUtc is { } last && state.EventTimeUtc < last) return false;

        user.StripeCustomerId = state.CustomerId;
        user.StripeSubscriptionId = state.Deleted ? null : state.SubscriptionId;
        user.PlanUpdatedAtUtc = state.EventTimeUtc;

        if (state.Deleted || state.Status is "canceled" or "incomplete_expired")
        {
            // Subskrypcja skończona po stronie dostawcy — koniec dostępu płatnego. Okres rozliczeniowy
            // planu darmowego liczymy dalej od kotwicy konta, więc nic tu nie zerujemy.
            user.PlanId = PlanIds.Free;
            user.PlanStatus = PlanStatuses.Canceled;
            user.PlanValidUntilUtc = null;
            return true;
        }

        switch (state.Status)
        {
            case "active" or "trialing":
                user.PlanId = options.PaidPlanId;
                user.PlanStatus = PlanStatuses.Active;
                user.PlanValidUntilUtc = state.CurrentPeriodEndUtc;
                // Limit ma odnawiać się RAZEM z płatnością, nie w przypadkowym dniu — dlatego kotwicą
                // okresu staje się początek okresu rozliczeniowego Stripe (E1 zostawił na to miejsce).
                if (state.CurrentPeriodStartUtc is { } start) user.BillingAnchorUtc = start;
                // Rezygnacja zgłoszona, ale okres opłacony — dostęp zostaje do jego końca.
                if (state.CancelAtPeriodEnd) user.PlanStatus = PlanStatuses.Canceled;
                return true;

            case "past_due" or "unpaid":
                // Karta odrzucona: dostęp zostaje na czas ponawiania obciążenia przez Stripe.
                user.PlanId = options.PaidPlanId;
                user.PlanStatus = PlanStatuses.PastDue;
                user.PlanValidUntilUtc =
                    (state.CurrentPeriodEndUtc ?? nowUtc).AddDays(Math.Max(0, options.GraceDays));
                return true;

            case "incomplete":
                // Pierwsza płatność jeszcze nie przeszła (np. 3D Secure w toku) — NIE nadajemy planu.
                // Konto zostaje na darmowym do czasu potwierdzenia.
                return true;

            default:
                // Nieznany status: nie zgadujemy w stronę hojności ani odcięcia — zostawiamy stan
                // uprawnienia bez zmian, żeby webhook nie mógł zaskoczyć nas nową wartością.
                return true;
        }
    }
}
