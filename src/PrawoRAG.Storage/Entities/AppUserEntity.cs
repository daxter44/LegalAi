using Microsoft.AspNetCore.Identity;

namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Konto użytkownika (E1/T-1). <see cref="IdentityUser.Id"/> to GUID w postaci tekstowej i to ON jest
/// tożsamością w całej domenie — rozmowy, analizy i feedback trzymają go w swojej tekstowej kolumnie
/// <c>UserId</c>. Dzięki temu zmiana adresu e-mail nie gubi historii, a wprowadzenie kont nie wymagało
/// migracji schematu poza tabelami Identity.
///
/// Wiersze sprzed wprowadzenia kont (alfa na kodach zaproszeń) mają w <c>UserId</c> nazwę testera —
/// zostają w bazie, ale nie należą do żadnego konta. Świadome: testerów było dwóch, a ewentualne
/// przypisanie to jednorazowy UPDATE, nie mechanizm w kodzie.
/// </summary>
public sealed class AppUserEntity : IdentityUser
{
    /// <summary>Nazwa pokazywana w interfejsie. Nie jest tożsamością — może się zmieniać.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Kiedy konto powstało (UTC) — na potrzeby wsparcia i statystyk rejestracji.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Data i wersja zaakceptowanego regulaminu w chwili rejestracji (E2/US-2.10 opiera się na tym polu).
    /// Trzymane na koncie, bo to najprostsze miejsce, w którym nie ginie przy czyszczeniu rozmów.
    /// </summary>
    public DateTime? TermsAcceptedAtUtc { get; set; }

    public string? TermsVersion { get; set; }

    // --- plan i uprawnienie (E1/T-8, T-9) -------------------------------------------------------
    // Stan uprawnienia trzymamy NA KONCIE, nie u dostawcy płatności: awaria Stripe nie może
    // odciąć dostępu komuś, kto zapłacił. E3 będzie te pola ustawiać z webhooków.

    /// <summary>Identyfikator planu (<c>free</c> / <c>pro</c>). Limity siedzą w konfiguracji.</summary>
    public string PlanId { get; set; } = "free";

    /// <summary>
    /// Stan uprawnienia: <c>active</c> / <c>past_due</c> / <c>canceled</c>. Dla planu darmowego
    /// zawsze <c>active</c> — darmowy nie wygasa.
    /// </summary>
    public string PlanStatus { get; set; } = "active";

    /// <summary>
    /// Do kiedy plan płatny obowiązuje. <c>null</c> = bezterminowo (plan darmowy). Po tej dacie
    /// konto spada do planu darmowego bez żadnego zadania w tle — sprawdzamy przy każdym zapytaniu.
    /// </summary>
    public DateTime? PlanValidUntilUtc { get; set; }

    /// <summary>
    /// Dzień, od którego liczy się okres rozliczeniowy. <c>null</c> = liczymy od
    /// <see cref="CreatedAtUtc"/>. E3 ustawi tu początek okresu subskrypcji, żeby limit odnawiał się
    /// razem z płatnością, a nie w przypadkowym dniu.
    /// </summary>
    public DateTime? BillingAnchorUtc { get; set; }
}
