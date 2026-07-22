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

    public AnalysisSession Create(string fileName, int pageCount, string prompt, IReadOnlyList<DocUnit> units, bool unitsTruncated)
    {
        Sweep();
        var session = new AnalysisSession(fileName, pageCount, prompt, units, unitsTruncated, time.GetUtcNow());
        _sessions[session.Id] = session;
        return session;
    }

    /// <summary>Null = sesja nie istnieje albo wygasła (dla UI to jeden przypadek: „sesja wygasła —
    /// rozpocznij analizę ponownie"). Trafienie odświeża TTL.</summary>
    public AnalysisSession? TryGet(Guid id)
    {
        Sweep();
        if (!_sessions.TryGetValue(id, out var session)) return null;
        session.Touch(time.GetUtcNow());
        return session;
    }

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (id, session) in _sessions)
            if (session.IsExpired(now, Ttl))
                _sessions.TryRemove(id, out _);
    }
}
