using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>
/// Adresy i tokeny w odnośnikach kont. Wydzielone z <see cref="AuthEndpoints"/>, bo to trzy rzeczy,
/// które muszą być POKRYTE TESTAMI: przekierowanie po zalogowaniu (open redirect), kodowanie tokenu
/// (zepsute = konto nie do odzyskania) i budowa adresu bazowego (podrobiony host = phishing).
/// </summary>
public static class AuthLinks
{
    /// <summary>Token Identity zawiera znaki spoza adresu URL — kodujemy base64url.</summary>
    public static string EncodeToken(string token) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    /// <summary>Odwrotność <see cref="EncodeToken"/>. Śmieci dają pusty token = odrzucenie przez Identity.</summary>
    public static string DecodeToken(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return ""; }
    }

    /// <summary>
    /// Przepuszcza WYŁĄCZNIE ścieżkę lokalną. Odrzuca adresy bezwzględne oraz „//obcy.host"
    /// i „/\obcy.host", które przeglądarka traktuje jak adres zewnętrzny — to klasyczny
    /// open redirect wykorzystywany w phishingu („zaloguj się i wróć do…").
    /// </summary>
    public static string? LocalOrNull(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : null;

    /// <summary>
    /// Adres bezwzględny do listu. Gdy skonfigurowano <c>Auth:PublicBaseUrl</c> — bierzemy go stamtąd,
    /// bo host z żądania zza proxy pochodzi z nagłówka, którym steruje klient (podrobiony odnośnik
    /// w cudzej skrzynce). Bez konfiguracji: adres z bieżącego żądania (dev).
    /// </summary>
    public static string Absolute(string? publicBaseUrl, string requestScheme, string requestHost, string path) =>
        string.IsNullOrWhiteSpace(publicBaseUrl)
            ? $"{requestScheme}://{requestHost}{path}"
            : $"{publicBaseUrl.TrimEnd('/')}{path}";
}
