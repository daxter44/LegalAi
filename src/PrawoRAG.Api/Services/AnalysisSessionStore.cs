using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace PrawoRAG.Api.Services;

/// <summary>
/// In-memory magazyn sesji analizy (SPK-2) — singleton; ŚWIADOMIE bez persystencji (treść załącznika
/// nie może dotknąć dysku/bazy — decyzja #1 planu DOC; restart = sesje znikają, UI komunikuje to
/// wprost). TTL liczony od ostatniego dostępu (<see cref="AnalysisSession.Touch"/>): aktywne dopytania
/// przedłużają życie sesji. Sprzątanie leniwe — przy każdej operacji, bez osobnego timera.
/// </summary>
public sealed class AnalysisSessionStore(TimeProvider time, IOptions<AnalysisOptions> options)
{
    private readonly ConcurrentDictionary<Guid, AnalysisSession> _sessions = new();

    private TimeSpan Ttl => TimeSpan.FromMinutes(options.Value.SessionTtlMinutes);

    public AnalysisSession Create(string userId, string fileName, int pageCount, string prompt, IReadOnlyList<DocUnit> units, bool unitsTruncated)
    {
        Sweep();
        var session = new AnalysisSession(userId, fileName, pageCount, prompt, units, unitsTruncated, time);
        _sessions[session.Id] = session;
        return session;
    }

    /// <summary>Null = sesja nie istnieje, wygasła ALBO należy do innego użytkownika — dla wołającego
    /// to jeden nierozróżnialny przypadek (id sesji widać w UI, więc sam Guid nie może być biletem
    /// do cudzego dokumentu). Trafienie odświeża TTL.</summary>
    public AnalysisSession? TryGet(Guid id, string userId)
    {
        Sweep();
        if (!_sessions.TryGetValue(id, out var session)) return null;
        if (!string.Equals(session.UserId, userId, StringComparison.Ordinal)) return null;
        session.Touch(time.GetUtcNow());
        return session;
    }

    public void Remove(Guid id)
    {
        if (_sessions.TryRemove(id, out var session)) session.Cancel();
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (id, session) in _sessions)
            if (session.IsExpired(now, Ttl) && _sessions.TryRemove(id, out var removed))
                removed.Cancel(); // wygasła sesja = anuluj ewentualny runner w locie
    }
}
