namespace PrawoRAG.Domain.Llm;

/// <summary>
/// Jedna ZAKOŃCZONA tura rozmowy (pytanie użytkownika + odpowiedź asystenta) — kontekst dla follow-upów.
/// <see cref="Answer"/> = null dla tur z abstynencją (pytanie nadal jest wartościowym kontekstem retrievalu,
/// ale nie ma treści odpowiedzi do pokazania modelowi).
/// <see cref="SourceAnchors"/> = etykiety/tytuły źródeł tamtej tury (np. „art. 157 § 1 KW", „Kodeks
/// wykroczeń") — czyste, ustrukturyzowane kotwice dla kontekstualizacji retrievalu follow-upu (anafora
/// „…z powyższej odpowiedzi" odnosi się do treści i źródeł poprzedniej tury, nie do samego pytania).
/// Opcjonalne: tor SSE bez metadanych źródeł degraduje się łagodnie do cytatów z tekstu odpowiedzi.
/// </summary>
public sealed record ChatTurn(string Question, string? Answer, IReadOnlyList<string>? SourceAnchors = null);
