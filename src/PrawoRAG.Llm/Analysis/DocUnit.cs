namespace PrawoRAG.Llm.Analysis;

/// <summary>Jednostka analizy dokumentu (SPK-1): logiczny fragment (§/art./pkt/akapit) z nagłówkiem
/// do wyświetlenia. <see cref="Text"/> zawiera nagłówek (czytelność w prompcie i w UI).</summary>
public sealed record DocUnit(int Index, string Heading, string Text);

/// <summary>Werdykt analizy jednej jednostki — parsowany z pierwszej linii odpowiedzi map-prompta
/// (albo nadany wprost: abstynencja → <see cref="NoSources"/>, wyjątek → <see cref="Error"/>).
/// Żyje w <c>PrawoRAG.Llm</c> (AJ-1a), żeby harness ewaluacji mógł go używać bez zależności od Api.</summary>
public enum UnitVerdict
{
    Unknown,
    Ok,
    /// <summary>Legacy (do 2026-09-03): „RYZYKO" bez wagi — zostaje dla odczytu starych rekordów i dla
    /// modeli, które zignorują nowy zestaw. Nowe werdykty: <see cref="RiskHigh"/> / <see cref="RiskLow"/>.</summary>
    Risk,
    /// <summary>Odmowa treściowa / bramka: źródła nie dają podstawy prawnej do oceny („BRAK PODSTAWY").</summary>
    NoSources,
    Error,
    /// <summary>AJ-5 (D3): ryzyko wysokie — postanowienie sprzeczne z normą bezwzględnie obowiązującą lub
    /// nieważne; wymaga zmiany.</summary>
    RiskHigh,
    /// <summary>AJ-5 (D3): ryzyko niskie — niekorzystne, wątpliwe lub do negocjacji, ale nie oczywiście
    /// sprzeczne z prawem.</summary>
    RiskLow,
    /// <summary>AJ-5 (D3): fragment bez twierdzenia prawnego do oceny (komparycja, dane stron, przedmiot).
    /// Dotąd lądował jako BRAK ŹRÓDEŁ i wyglądał jak awaria.</summary>
    NoLegalContent,
    /// <summary>AJ-5 (D3): fragment opiera się na dokumencie poza korpusem (akt prawa miejscowego,
    /// załącznik, regulamin zewnętrzny) — ocena wymaga tego dokumentu, nie lepszego retrievalu.</summary>
    OutOfScope,
}

public static class UnitVerdictExtensions
{
    /// <summary>Każde ryzyko (legacy + wysokie + niskie) — dla chipów, otwierania kart i metryk.</summary>
    public static bool IsRisk(this UnitVerdict v) => v is UnitVerdict.Risk or UnitVerdict.RiskHigh or UnitVerdict.RiskLow;
}
