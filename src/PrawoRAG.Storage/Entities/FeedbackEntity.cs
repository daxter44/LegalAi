namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Ocena prawnika do odpowiedzi asystenta — pętla danych do strojenia (rosnący golden set).
/// <see cref="Verdict"/>: „up" | „down" | „wrong-answer" | „needless-refusal".
/// </summary>
public class FeedbackEntity
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public MessageEntity? Message { get; set; }

    /// <summary>Autor oceny (tożsamość z OIDC).</summary>
    public required string UserId { get; set; }

    public required string Verdict { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
