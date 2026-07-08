namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Jedna ZAKOŃCZONA tura rozmowy (pytanie użytkownika + odpowiedź asystenta) — kontekst dla follow-upów.
/// <see cref="Answer"/> = null dla tur z abstynencją (pytanie nadal jest wartościowym kontekstem retrievalu,
/// ale nie ma treści odpowiedzi do pokazania modelowi).
/// </summary>
public sealed record ChatTurn(string Question, string? Answer);
