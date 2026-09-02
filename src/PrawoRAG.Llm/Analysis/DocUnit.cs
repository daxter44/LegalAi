namespace PrawoRAG.Llm.Analysis;

/// <summary>Jednostka analizy dokumentu (SPK-1): logiczny fragment (§/art./pkt/akapit) z nagłówkiem
/// do wyświetlenia. <see cref="Text"/> zawiera nagłówek (czytelność w prompcie i w UI).</summary>
public sealed record DocUnit(int Index, string Heading, string Text);

/// <summary>Werdykt analizy jednej jednostki — parsowany z pierwszej linii odpowiedzi map-prompta
/// (albo nadany wprost: abstynencja → <see cref="NoSources"/>, wyjątek → <see cref="Error"/>).
/// Żyje w <c>PrawoRAG.Llm</c> (AJ-1a), żeby harness ewaluacji mógł go używać bez zależności od Api.</summary>
public enum UnitVerdict { Unknown, Ok, Risk, NoSources, Error }
