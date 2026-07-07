namespace PrawoRAG.Storage.Entities;

/// <summary>
/// Rozmowa użytkownika (demo). <see cref="UserId"/> = hak pod user scope — każdy widzi tylko swoje
/// (filtr po stronie serwera, nigdy po id z klienta).
/// </summary>
public class ConversationEntity
{
    public Guid Id { get; set; }

    /// <summary>Tożsamość właściciela (claim z OIDC, np. e-mail/sub).</summary>
    public required string UserId { get; set; }

    public string Title { get; set; } = "Nowa rozmowa";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<MessageEntity> Messages { get; set; } = [];
}
