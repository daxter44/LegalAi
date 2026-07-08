using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Twardy dzienny cap kosztów LLM (3.7) — działa OBOK <see cref="RateGuard"/> (inna oś: RateGuard =
/// okno minutowe przeciw pętli; CostGuard = dzienne budżety przeciw wyczerpaniu portfela). Trzy limity
/// z <see cref="AccessOptions"/>: zapytania/dzień per tester, zapytania/dzień globalnie, globalny budżet
/// znaków WYJŚCIA (proxy tokenów — providerzy streamują tekst bez liczników). Klucz dnia = data UTC.
/// Świadome ograniczenie: liczniki in-memory — restart zeruje dzień (dla zamkniętego testu akceptowalne).
/// Gdy Access:Enabled=false — zawsze przepuszcza (zero zmian zachowania dev/M4).
/// </summary>
public sealed class CostGuard(IOptions<AccessOptions> options, TimeProvider time)
{
    private readonly object _lock = new();
    private DateOnly _day;
    private long _globalRequests;
    private long _globalOutputChars;
    private readonly ConcurrentDictionary<string, int> _userRequests = new();

    /// <summary>True = można wykonać zapytanie LLM; false = twardy limit dzienny (reason mówi który).</summary>
    public bool TryAcquire(string userId, out string? reason)
    {
        reason = null;
        var o = options.Value;
        if (!o.Enabled) return true;

        lock (_lock)
        {
            RollOverIfNewDay();

            if (_globalOutputChars >= o.MaxGlobalOutputCharsPerDay)
            { reason = "globalny dzienny budżet odpowiedzi"; return false; }
            if (_globalRequests >= o.MaxGlobalRequestsPerDay)
            { reason = "globalny dzienny limit zapytań"; return false; }
            if (_userRequests.GetValueOrDefault(userId) >= o.MaxUserRequestsPerDay)
            { reason = "Twój dzienny limit zapytań"; return false; }

            _globalRequests++;
            _userRequests.AddOrUpdate(userId, 1, (_, n) => n + 1);
            return true;
        }
    }

    /// <summary>Dolicza rozmiar wyjścia LLM po zakończeniu streamu (budżet znaków).</summary>
    public void Record(string userId, int outputChars)
    {
        if (!options.Value.Enabled || outputChars <= 0) return;
        lock (_lock)
        {
            RollOverIfNewDay();
            _globalOutputChars += outputChars;
        }
    }

    /// <summary>Komunikat dla użytkownika przy odmowie (spójny UI/SSE).</summary>
    public static string LimitMessage(string? reason) =>
        $"Wyczerpany {reason ?? "dzienny limit"} — spróbuj ponownie jutro. To zamknięty test z twardym budżetem.";

    private void RollOverIfNewDay()
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (today == _day) return;
        _day = today;
        _globalRequests = 0;
        _globalOutputChars = 0;
        _userRequests.Clear();
    }
}
