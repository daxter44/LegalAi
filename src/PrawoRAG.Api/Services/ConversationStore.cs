using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Api.Services;

/// <summary>Trwały zapis rozmów, wiadomości i feedbacku (FE-4). Materiał do golden setu i kalibracji.</summary>
public interface IConversationStore
{
    Task<Guid> CreateConversationAsync(string userId, string title, CancellationToken ct);
    Task<Guid> AddUserMessageAsync(Guid conversationId, string content, CancellationToken ct);
    Task<Guid> AddAssistantMessageAsync(Guid conversationId, string content,
        IReadOnlyList<ChatSource> sources, bool abstained, bool? citationClean, string? model, CancellationToken ct);
    Task AddFeedbackAsync(Guid messageId, string userId, string verdict, string? note, CancellationToken ct);
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
        var json = sources.Count == 0 ? null : JsonSerializer.SerializeToDocument(
            sources.Select(s => new { s.Index, s.Label, s.Url }));
        return await AddMessageAsync(conversationId, "assistant", content, json, abstained, citationClean, model, ct);
    }

    public async Task AddFeedbackAsync(Guid messageId, string userId, string verdict, string? note, CancellationToken ct)
    {
        await using var db = Db();
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
