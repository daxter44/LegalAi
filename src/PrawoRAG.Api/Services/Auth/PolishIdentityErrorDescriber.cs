using Microsoft.AspNetCore.Identity;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Komunikaty Identity po polsku — trafiają wprost na ekran rejestracji i resetu, więc angielskie
/// zdania byłyby widoczne dla klienta. Tłumaczymy tylko te, które realnie mogą się pokazać przy
/// naszej konfiguracji (hasło, adres, token).
/// </summary>
public sealed class PolishIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) => new()
    {
        Code = nameof(PasswordTooShort),
        Description = $"Hasło musi mieć co najmniej {length} znaków.",
    };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new()
    {
        Code = nameof(PasswordRequiresUniqueChars),
        Description = $"Hasło musi zawierać co najmniej {uniqueChars} różnych znaków.",
    };

    public override IdentityError PasswordRequiresDigit() => new()
    {
        Code = nameof(PasswordRequiresDigit),
        Description = "Hasło musi zawierać cyfrę.",
    };

    public override IdentityError PasswordRequiresLower() => new()
    {
        Code = nameof(PasswordRequiresLower),
        Description = "Hasło musi zawierać małą literę.",
    };

    public override IdentityError PasswordRequiresUpper() => new()
    {
        Code = nameof(PasswordRequiresUpper),
        Description = "Hasło musi zawierać wielką literę.",
    };

    public override IdentityError PasswordRequiresNonAlphanumeric() => new()
    {
        Code = nameof(PasswordRequiresNonAlphanumeric),
        Description = "Hasło musi zawierać znak specjalny.",
    };

    public override IdentityError InvalidEmail(string? email) => new()
    {
        Code = nameof(InvalidEmail),
        Description = "Adres e-mail jest nieprawidłowy.",
    };

    // Uwaga: przy rejestracji NIE pokazujemy tego komunikatu — zajęty adres kończy się tą samą
    // stroną co sukces (ochrona przed wyliczaniem kont). Zostaje dla ścieżek administracyjnych.
    public override IdentityError DuplicateEmail(string email) => new()
    {
        Code = nameof(DuplicateEmail),
        Description = "Tego adresu nie można użyć.",
    };

    public override IdentityError DuplicateUserName(string userName) => new()
    {
        Code = nameof(DuplicateUserName),
        Description = "Tego adresu nie można użyć.",
    };

    public override IdentityError InvalidToken() => new()
    {
        Code = nameof(InvalidToken),
        Description = "Odnośnik jest nieaktualny lub został już wykorzystany. Poproś o nowy.",
    };

    public override IdentityError PasswordMismatch() => new()
    {
        Code = nameof(PasswordMismatch),
        Description = "Nieprawidłowe hasło.",
    };
}
