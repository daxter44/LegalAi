using System.Collections.Concurrent;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Prosty limiter kosztu dla ścieżki interaktywnej (Blazor/SignalR — poza zasięgiem middleware HTTP rate
/// limitera): przesuwane okno na użytkownika. Chroni przed pętlą i runaway kosztem LLM na demo (C7/FE-7.4).
/// </summary>
public sealed class RateGuard
{
    private const int MaxPerWindow = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    /// <summary>True = można wykonać zapytanie; false = przekroczono limit w oknie.</summary>
    public bool TryAcquire(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        var q = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > Window) q.Dequeue();
            if (q.Count >= MaxPerWindow) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
