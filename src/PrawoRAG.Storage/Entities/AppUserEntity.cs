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
}
