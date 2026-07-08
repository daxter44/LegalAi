using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services;

/// <summary>Nagłówek rozmowy na listę w sidebarze (bez treści).</summary>
public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

/// <summary>Wiadomość wczytana z historii — do odtworzenia rozmowy w UI.</summary>
public sealed record StoredMessage(
    Guid Id, string Role, string Content, IReadOnlyList<ChatSource> Sources,
    bool Abstained, bool? CitationClean, string? Model);

/// <summary>Trwały zapis i ODCZYT rozmów, wiadomości i feedbacku (FE-4). Materiał do golden setu
/// i kalibracji + historia czatów w UI. Odczyt zawsze filtrowany po <c>userId</c> po stronie serwera
/// (tester nie otworzy cudzej rozmowy po zgadnięciu ID).</summary>
public interface IConversationStore
{
    Task<Guid> CreateConversationAsync(string userId, string title, CancellationToken ct);
    Task<Guid> AddUserMessageAsync(Guid conversationId, string content, CancellationToken ct);
    Task<Guid> AddAssistantMessageAsync(Guid conversationId, string content,
        IReadOnlyList<ChatSource> sources, bool abstained, bool? citationClean, string? model, CancellationToken ct);
    Task AddFeedbackAsync(Guid messageId, string userId, string verdict, string? note, CancellationToken ct);

    /// <summary>Rozmowy użytkownika, najnowsze pierwsze.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(string userId, int limit, CancellationToken ct);

    /// <summary>Wiadomości rozmowy chronologicznie; pusta lista, gdy rozmowa nie należy do usera.</summary>
    Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(Guid conversationId, string userId, CancellationToken ct);
}

/// <summary>
/// Tworzy krótkożyciowy <see cref="PrawoRagDbContext"/> per operacja (przez scope factory) — unika
/// dzielenia jednego, długożyciowego kontekstu w obwodzie Blazor Server (DbContext nie jest thread-safe).
/// </summary>
public sealed class ConversationStore(IServiceScopeFactory scopeFactory) : IConversationStore
{
    public async Task<Guid> CreateConversationAsync(string userId, string title, CancellationToken ct)
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var c = new ConversationEntity
        {
            Id = Guid.CreateVersion7(), UserId = userId,
            Title = Trunc(title, 300), CreatedAt = now, UpdatedAt = now,
        };
        db.Conversations.Add(c);
        await db.SaveChangesAsync(ct);
        return c.Id;
    }

    public async Task<Guid> AddUserMessageAsync(Guid conversationId, string content, CancellationToken ct)
        => await AddMessageAsync(conversationId, "user", content, sources: null, abstained: false, citationClean: null, model: null, ct);

    public async Task<Guid> AddAssistantMessageAsync(Guid conversationId, string content,
        IReadOnlyList<ChatSource> sources, bool abstained, bool? citationClean, string? model, CancellationToken ct)
    {
        // Pełny ChatSource (nie tylko Index/Label/Url jak dawniej) — żeby wczytana rozmowa miała
        // kompletny panel źródeł (snippet, tytuł, chip nowelizacji). Stare wpisy czyta tolerancyjnie
        // ParseSources (brakujące pola → puste).
        var json = sources.Count == 0 ? null : JsonSerializer.SerializeToDocument(sources);
        return await AddMessageAsync(conversationId, "assistant", content, json, abstained, citationClean, model, ct);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(string userId, int limit, CancellationToken ct)
    {
        await using var db = Db();
        return await db.Conversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(Guid conversationId, string userId, CancellationToken ct)
    {
        await using var db = Db();
        var rows = await db.Messages
            .Where(m => m.ConversationId == conversationId && m.Conversation!.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(m => new StoredMessage(
            m.Id, m.Role, m.Content, ParseSources(m.RetrievedSources),
            m.Abstained, m.CitationClean, m.Model)).ToList();
    }

    /// <summary>
    /// Tolerancyjny odczyt źródeł z jsonb: nowe wpisy = pełny <see cref="ChatSource"/>; stare (sprzed
    /// rozszerzenia zapisu) mają tylko Index/Label/Url — brakujące pola stają się puste. Czysta — testowalna.
    /// </summary>
    public static IReadOnlyList<ChatSource> ParseSources(JsonDocument? json)
    {
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ChatSource>();
        foreach (var el in json.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            result.Add(new ChatSource(
                Index: Prop(el, "Index") is { ValueKind: JsonValueKind.Number } i ? i.GetInt32() : 0,
                Label: Str(el, "Label") ?? "",
                Title: Str(el, "Title") ?? "",
                Url: Str(el, "Url"),
                Snippet: Str(el, "Snippet") ?? "",
                AmendmentEffectiveDate: Str(el, "AmendmentEffectiveDate")));
        }
        return result;

        static JsonElement? Prop(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) ? v : null;
        static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public async Task AddFeedbackAsync(Guid messageId, string userId, string verdict, string? note, CancellationToken ct)
    {
        await using var db = Db();
        // Ocena tylko WŁASNEJ wiadomości (spójnie z odczytem: filtr po UserId po stronie serwera) —
        // cudzy messageId nie zaśmieca danych kalibracyjnych.
        var owns = await db.Messages.AnyAsync(
            m => m.Id == messageId && m.Conversation!.UserId == userId, ct);
        if (!owns) return;

        db.Feedbacks.Add(new FeedbackEntity
        {
            Id = Guid.CreateVersion7(), MessageId = messageId, UserId = userId,
            Verdict = verdict, Note = note, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> AddMessageAsync(Guid conversationId, string role, string content,
        JsonDocument? sources, bool abstained, bool? citationClean, string? model, CancellationToken ct)
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var m = new MessageEntity
        {
            Id = Guid.CreateVersion7(), ConversationId = conversationId, Role = role, Content = content,
            CreatedAt = now, RetrievedSources = sources, Abstained = abstained, CitationClean = citationClean, Model = model,
        };
        db.Messages.Add(m);
        await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.UpdatedAt, now), ct);
        await db.SaveChangesAsync(ct);
        return m.Id;
    }

    private PrawoRagDbContext Db() => scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PrawoRagDbContext>();

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
