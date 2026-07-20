using System.Text.RegularExpressions;

namespace PrawoRAG.Domain.Retrieval;

/// <summary>
/// Wykrywanie tokenów akronimopodobnych w pytaniu (JAK-5b, Case 4 raportu odmów: „KSeF").
/// Mechanika problemu: użytkownik pyta żargonem („obowiązkowy KSEF w 2026"), a websearch_to_tsquery
/// AND-uje WSZYSTKIE słowa pytania — chunki zawierające akronim, ale nie „obowiązkowy"/„2026",
/// odpadają z toru rzadkiego, a embedding nie generalizuje skrótu na pełną nazwę. Wykryty akronim
/// dostaje w retrieverze OSOBNE, jednotokenowe zapytanie leksykalne (tsvector jest case-insensitive,
/// więc „KSEF" znajdzie „KSeF").
///
/// Świadomie BEZ kuratorowanej listy skrótów (feedback właściciela: lista zawsze przegra z życiem —
/// jutro wejdzie nowy skrót). Heurystyka czysto formalna: ≥2 wielkie litery w tokenie 2–8 znaków.
/// Known-limitation: skrót żywy w żargonie, którego korpus NIGDZIE nie używa (np. CUW), nie zostanie
/// uratowany żadnym torem leksykalnym — to granica podejścia bez LLM.
/// </summary>
public static class AcronymDetector
{
    /// <summary>Token słowny 2–8 liter (KSeF, RODO, VAT, ZUS — także z małymi literami w środku).</summary>
    private static readonly Regex TokenRe = new(@"(?<![\p{L}\d])\p{L}{2,8}(?![\p{L}\d])", RegexOptions.Compiled);

    private static readonly Regex UpperRe = new(@"[A-ZĄĆĘŁŃÓŚŹŻ]", RegexOptions.Compiled);

    /// <summary>Liczebniki rzymskie (sygnatury „II AKa…", numery wydziałów) — dwie wielkie litery,
    /// ale zerowa wartość jako samodzielne zapytanie (matchują wszędzie).</summary>
    private static readonly Regex RomanRe = new(@"^[IVXLCDM]+$", RegexOptions.Compiled);

    /// <summary>Akronimy z pytania, w kolejności wystąpienia, bez duplikatów (case-insensitive).
    /// Pusta lista dla pytań bez akronimów — tor akronimowy w retrieverze nic wtedy nie kosztuje.</summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tokens = TokenRe.Matches(text).Select(m => m.Value).ToList();
        if (tokens.Count == 0) return [];

        // Caps-lock guard: gdy większość tokenów wygląda „akronimowo", to krzyczące pytanie,
        // nie żargon — wykrywanie wyłączone (inaczej każde słowo stałoby się torem).
        var acronymLike = tokens.Where(IsAcronymLike).ToList();
        if (acronymLike.Count * 2 > tokens.Count) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return acronymLike.Where(t => seen.Add(t)).ToList();
    }

    private static bool IsAcronymLike(string token) =>
        UpperRe.Matches(token).Count >= 2 && !RomanRe.IsMatch(token);
}
