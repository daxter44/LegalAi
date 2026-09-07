namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Jedna ZAKOŃCZONA tura rozmowy (pytanie użytkownika + odpowiedź asystenta) — kontekst dla follow-upów.
/// <see cref="Answer"/> = null dla tur z abstynencją (pytanie nadal jest wartościowym kontekstem retrievalu,
/// ale nie ma treści odpowiedzi do pokazania modelowi).
/// Kotwice źródeł (etykiety/tytuły źródeł tamtej tury) USUNIĘTE 2026-08-11: niosły numer Dz.U. i numer
/// artykułu poprzedniej tury, więc w każdej ścieżce bez ExactMatchText wyzwalały tory DOKŁADNE i zjadały
/// cały budżet slotów aktem z poprzedniej tury — patrz <see cref="PrawoRAG.Domain.Retrieval.FollowUpQuery"/>.
/// Kontekstualizacja follow-upu foldu­je teraz wyłącznie cytaty i fragment tekstu odpowiedzi.
/// </summary>
public sealed record ChatTurn(string Question, string? Answer);
