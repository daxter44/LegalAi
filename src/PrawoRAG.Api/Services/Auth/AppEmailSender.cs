using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PrawoRAG.Api.Services.Auth;

/// <summary>Wysyłka jednego listu transakcyjnego. Rzuca, gdy nie udało się nadać.</summary>
public interface IAppEmailSender
{
    Task SendAsync(string toEmail, EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Wysyłka przez API Resend (https://api.resend.com/emails).
/// Bezpieczeństwo: klucz API bierzemy z konfiguracji środowiska i NIGDY nie trafia do logu ani do
/// komunikatu wyjątku; przy błędzie logujemy status i skrócone ciało odpowiedzi.
/// </summary>
public sealed class ResendEmailSender(
    HttpClient http,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailSender> log) : IAppEmailSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(string toEmail, EmailMessage message, CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey) || string.IsNullOrWhiteSpace(o.From))
            throw new InvalidOperationException("Email:Provider=resend wymaga Email:ApiKey i Email:From.");

        var payload = new Dictionary<string, object>
        {
            ["from"] = o.From,
            ["to"] = new[] { toEmail },
            ["subject"] = message.Subject,
            ["html"] = message.Html,
            ["text"] = message.Text,
        };
        if (!string.IsNullOrWhiteSpace(o.ReplyTo)) payload["reply_to"] = o.ReplyTo;

        using var req = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return;

        // Ciało odpowiedzi bywa echem żądania — przycinamy i nie logujemy nagłówków (tam jest klucz).
        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > 500) body = body[..500];
        log.LogError("Resend odrzucił wysyłkę: {Status}. Odpowiedź: {Body}", (int)res.StatusCode, body);
        throw new InvalidOperationException($"Wysyłka poczty nie powiodła się (HTTP {(int)res.StatusCode}).");
    }
}

/// <summary>
/// Nadawca zastępczy: nic nie wysyła, zapisuje list do logu. Domyślny w dev — pozwala przejść pełną
/// ścieżkę rejestracji i resetu bez konta u dostawcy. Treść (z odnośnikiem zawierającym token) trafia
/// do logu, więc PRODUKCJA MUSI używać "resend": log z tokenem to log z możliwością przejęcia konta.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> log, IHostEnvironment env) : IAppEmailSender
{
    public Task SendAsync(string toEmail, EmailMessage message, CancellationToken ct = default)
    {
        if (env.IsDevelopment())
            log.LogWarning("[POCZTA-DEV] do={To} temat={Subject}\n{Text}", toEmail, message.Subject, message.Text);
        else
            // Poza dev nie wypisujemy treści: mógłby to być odnośnik resetu w logu produkcyjnym.
            log.LogError("Email:Provider=log poza środowiskiem dev — list do {To} NIE ZOSTAŁ wysłany. " +
                         "Ustaw Email:Provider=resend.", toEmail);
        return Task.CompletedTask;
    }
}
