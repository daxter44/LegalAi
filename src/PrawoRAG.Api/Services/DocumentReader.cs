using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;

namespace PrawoRAG.Api.Services;

/// <summary>Dokument do podglądu na stronie (widok „/dokument/{id}"): metadane + treść złożona z chunków.
/// Korpus jest PUBLICZNY (nie per-użytkownik), więc bez scopingu po UserId (inaczej niż rozmowy).</summary>
public sealed record DocumentView(
    Guid Id, string Title, string DocType, string? Url,
    string? CaseNumber, DateOnly? JudgmentDate, string? Court,
    IReadOnlyList<string> LegalBases, IReadOnlyList<DocumentSection> Sections);

public interface IDocumentReader
{
    Task<DocumentView?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>Czyta dokument + jego chunki (po ChunkIndex) i składa do widoku. DbContext per operacja
/// (scope factory) — jak <see cref="ConversationStore"/>.</summary>
public sealed class DocumentReader(IServiceScopeFactory scopeFactory) : IDocumentReader
{
    public async Task<DocumentView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrawoRagDbContext>();

        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return null;

        var chunks = await db.Chunks
            .Where(c => c.DocumentId == id)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.Section, c.Text })
            .ToListAsync(ct);

        var sections = DocumentBody.Assemble(chunks.Select(c => (c.Section, c.Text)).ToList());
        return new DocumentView(
            doc.Id, doc.Title, doc.DocType, doc.SourceUrl,
            doc.CaseNumber, doc.JudgmentDate, Str(doc.TypedMetadata, "court"),
            LegalBases(doc.TypedMetadata), sections);
    }

    /// <summary>Podstawy prawne (pole „text" obiektów <c>referencedRegulations</c>) — jak w retrieverze.</summary>
    private static IReadOnlyList<string> LegalBases(JsonDocument? meta)
    {
        if (meta is null || meta.RootElement.ValueKind != JsonValueKind.Object ||
            !meta.RootElement.TryGetProperty("referencedRegulations", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("text", out var t) &&
                t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }

    private static string? Str(JsonDocument? meta, string name) =>
        meta is not null && meta.RootElement.ValueKind == JsonValueKind.Object &&
        meta.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
