using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services;

/// <summary>Nagłówek analizy na listę w sidebarze (bez treści).</summary>
public sealed record AnalysisSummaryRow(Guid Id, string FileName, string Prompt, AnalysisStatus Status, DateTimeOffset UpdatedAt);

/// <summary>Raport wczytany z bazy — BEZ treści jednostek (persystujemy tylko raport, decyzja DOC #1).</summary>
public sealed record StoredAnalysis(
    Guid Id, string FileName, int PageCount, string Prompt, AnalysisStatus Status,
    int UnitsTotal, bool UnitsTruncated, string? Summary, string? Error,
    IReadOnlyList<StoredUnit> Units);

public sealed record StoredUnit(
    Guid Id, int UnitIndex, string Heading, UnitVerdict Verdict, string? Answer,
    IReadOnlyList<ChatSource> Sources, bool? CitationClean, string? Error, string? FeedbackGiven);

/// <summary>
/// Trwały zapis i odczyt RAPORTÓW analizy dokumentów (AN-3) — wzorzec <see cref="IConversationStore"/>:
/// odczyt zawsze filtrowany po <c>userId</c> po stronie serwera; wołający traktuje zapisy jako
/// best-effort (awaria persystencji nie może zablokować analizy). Treść dokumentu klienta NIGDY
/// nie przechodzi przez ten interfejs.
/// </summary>
public interface IAnalysisStore
{
    Task CreateAsync(Guid id, string userId, string fileName, int pageCount, string prompt,
        int unitsTotal, bool unitsTruncated, CancellationToken ct);

    /// <summary>Insert-or-update po kluczu naturalnym (AnalysisId, UnitIndex) — retry nadpisuje.</summary>
    Task UpsertUnitAsync(Guid analysisId, UnitAnalysis unit, CancellationToken ct);

    Task CompleteAsync(Guid analysisId, string? summary, CancellationToken ct);
    Task FailAsync(Guid analysisId, string error, CancellationToken ct);

    /// <summary>Analyzing → Interrupted; rekordów w stanach terminalnych NIE dotyka (warunkowy update).</summary>
    Task MarkInterruptedAsync(Guid analysisId, CancellationToken ct);

    /// <summary>Sweep na starcie procesu: po restarcie żaden rekord Analyzing nie może być prawdziwy
    /// (sesje in-memory zginęły). Zwraca liczbę oznaczonych.</summary>
    Task<int> MarkAllInterruptedAsync(CancellationToken ct);

    /// <summary>Analizy użytkownika, najnowsze pierwsze.</summary>
    Task<IReadOnlyList<AnalysisSummaryRow>> ListAsync(string userId, int limit, CancellationToken ct);

    /// <summary>Null, gdy analiza nie istnieje albo należy do innego użytkownika.</summary>
    Task<StoredAnalysis?> GetAsync(Guid id, string userId, CancellationToken ct);

    /// <summary>Ocena tylko WŁASNEJ jednostki (własność przez join do analyses.UserId); jednorazowa.</summary>
    Task AddUnitFeedbackAsync(Guid analysisUnitId, string userId, string verdict, string? note, CancellationToken ct);
}

/// <summary>Krótkożyciowy DbContext per operacja (scope factory) — jak <see cref="ConversationStore"/>;
/// singleton, bo woła go singleton <see cref="AnalysisRunner"/> spoza obwodu Blazora.</summary>
public sealed class AnalysisStore(IServiceScopeFactory scopeFactory) : IAnalysisStore
{
    public async Task CreateAsync(Guid id, string userId, string fileName, int pageCount, string prompt,
        int unitsTotal, bool unitsTruncated, CancellationToken ct)
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        db.Analyses.Add(new AnalysisEntity
        {
            Id = id, UserId = userId, FileName = Trunc(fileName, 300), PageCount = pageCount,
            Prompt = prompt, Status = nameof(AnalysisStatus.Analyzing),
            UnitsTotal = unitsTotal, UnitsTruncated = unitsTruncated,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertUnitAsync(Guid analysisId, UnitAnalysis unit, CancellationToken ct)
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var row = await db.AnalysisUnits.FirstOrDefaultAsync(
            u => u.AnalysisId == analysisId && u.UnitIndex == unit.Index, ct);
        if (row is null)
        {
            row = new AnalysisUnitEntity
            {
                Id = Guid.CreateVersion7(), AnalysisId = analysisId, UnitIndex = unit.Index,
                Heading = Trunc(unit.Heading, 200), Verdict = unit.Verdict.ToString(), CreatedAt = now,
            };
            db.AnalysisUnits.Add(row);
        }
        row.Heading = Trunc(unit.Heading, 200);
        row.Verdict = unit.Verdict.ToString();
        row.Answer = unit.Answer;
        row.Sources = unit.Sources.Count == 0 ? null : JsonSerializer.SerializeToDocument(unit.Sources);
        row.CitationClean = unit.Check?.IsClean;
        row.Error = unit.Error is { } e ? Trunc(e, 2000) : null;
        await db.Analyses.Where(a => a.Id == analysisId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedAt, now), ct);
        await db.SaveChangesAsync(ct);
    }

    public Task CompleteAsync(Guid analysisId, string? summary, CancellationToken ct) =>
        SetTerminalAsync(analysisId, nameof(AnalysisStatus.Done), summary, error: null, ct);

    public Task FailAsync(Guid analysisId, string error, CancellationToken ct) =>
        SetTerminalAsync(analysisId, nameof(AnalysisStatus.Failed), summary: null, Trunc(error, 2000), ct);

    public async Task MarkInterruptedAsync(Guid analysisId, CancellationToken ct)
    {
        await using var db = Db();
        await db.Analyses
            .Where(a => a.Id == analysisId && a.Status == nameof(AnalysisStatus.Analyzing))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, nameof(AnalysisStatus.Interrupted))
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<int> MarkAllInterruptedAsync(CancellationToken ct)
    {
        await using var db = Db();
        return await db.Analyses
            .Where(a => a.Status == nameof(AnalysisStatus.Analyzing))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, nameof(AnalysisStatus.Interrupted))
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<IReadOnlyList<AnalysisSummaryRow>> ListAsync(string userId, int limit, CancellationToken ct)
    {
        await using var db = Db();
        var rows = await db.Analyses
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .Select(a => new { a.Id, a.FileName, a.Prompt, a.Status, a.UpdatedAt })
            .ToListAsync(ct);
        return rows.Select(a => new AnalysisSummaryRow(
            a.Id, a.FileName, a.Prompt, ParseStatus(a.Status), a.UpdatedAt)).ToList();
    }

    public async Task<StoredAnalysis?> GetAsync(Guid id, string userId, CancellationToken ct)
    {
        await using var db = Db();
        var a = await db.Analyses
            .Where(x => x.Id == id && x.UserId == userId)
            .Include(x => x.Units)
            .ThenInclude(u => u.Feedback)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
        if (a is null) return null;

        var units = a.Units
            .OrderBy(u => u.UnitIndex)
            .Select(u => new StoredUnit(
                u.Id, u.UnitIndex, u.Heading, ParseVerdict(u.Verdict), u.Answer,
                ConversationStore.ParseSources(u.Sources), u.CitationClean, u.Error,
                u.Feedback?.Verdict))
            .ToList();
        return new StoredAnalysis(
            a.Id, a.FileName, a.PageCount, a.Prompt, ParseStatus(a.Status),
            a.UnitsTotal, a.UnitsTruncated, a.Summary, a.Error, units);
    }

    public async Task AddUnitFeedbackAsync(Guid analysisUnitId, string userId, string verdict, string? note, CancellationToken ct)
    {
        await using var db = Db();
        var owns = await db.AnalysisUnits.AnyAsync(
            u => u.Id == analysisUnitId && u.Analysis!.UserId == userId, ct);
        if (!owns) return;
        var already = await db.AnalysisUnitFeedbacks.AnyAsync(f => f.AnalysisUnitId == analysisUnitId, ct);
        if (already) return; // 1:1 — pierwsza ocena wiąże

        db.AnalysisUnitFeedbacks.Add(new AnalysisUnitFeedbackEntity
        {
            Id = Guid.CreateVersion7(), AnalysisUnitId = analysisUnitId, UserId = userId,
            Verdict = verdict, Note = note, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SetTerminalAsync(Guid analysisId, string status, string? summary, string? error, CancellationToken ct)
    {
        await using var db = Db();
        await db.Analyses.Where(a => a.Id == analysisId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.Summary, summary)
                .SetProperty(a => a.Error, error)
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>Tolerancyjny odczyt enuma zapisanego jako string — nieznana wartość (stary/przyszły
    /// zapis) degraduje się bezpiecznie zamiast rzucać.</summary>
    private static AnalysisStatus ParseStatus(string s) =>
        Enum.TryParse<AnalysisStatus>(s, out var v) ? v : AnalysisStatus.Interrupted;

    private static UnitVerdict ParseVerdict(string s) =>
        Enum.TryParse<UnitVerdict>(s, out var v) ? v : UnitVerdict.Unknown;

    private PrawoRagDbContext Db() => scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PrawoRagDbContext>();

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
