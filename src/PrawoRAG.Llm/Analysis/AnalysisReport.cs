using System.Text;

namespace PrawoRAG.Llm.Analysis;

/// <summary>
/// Nagłówek raportu analizy (AJ-6) liczony MECHANICZNIE z werdyktów — bez LLM. Odpowiada na
/// pytanie „ile i które fragmenty" zanim użytkownik przeczyta streszczenie, a streszczenie LLM
/// dostaje go jako jedyną podstawę meta-wniosku (D2): model może wnioskować Z WERDYKTÓW, ale nie
/// dokładać nowych twierdzeń prawnych. Ten sam tekst idzie do kotwicy dopytań, więc pytania
/// przekrojowe („które paragrafy są najgroźniejsze?") mają odpowiedź w kontekście bez retrievalu.
/// </summary>
public static class AnalysisReport
{
    /// <summary>Np. „3 z 14 fragmentów z ryzykiem (wysokie: § 5, § 7; niskie: § 12); 2 poza zakresem
    /// korpusu; 1 bez podstawy w źródłach; 4 bez treści prawnej; 1 z błędem". Pusta lista → "".</summary>
    public static string Headline(IReadOnlyList<UnitDigest> results)
    {
        if (results.Count == 0) return "";
        var total = results.Count;
        var high = results.Where(r => r.Verdict is UnitVerdict.RiskHigh or UnitVerdict.Risk).Select(r => r.Heading).ToList();
        var low = results.Where(r => r.Verdict == UnitVerdict.RiskLow).Select(r => r.Heading).ToList();
        var risk = high.Count + low.Count;

        var sb = new StringBuilder();
        if (risk == 0)
            sb.Append($"Brak fragmentów z ryzykiem wśród {total} {Fragments(total)}");
        else
        {
            sb.Append($"{risk} z {total} {Fragments(total)} z ryzykiem (");
            var parts = new List<string>();
            if (high.Count > 0) parts.Add("wysokie: " + string.Join(", ", high));
            if (low.Count > 0) parts.Add("niskie: " + string.Join(", ", low));
            sb.Append(string.Join("; ", parts)).Append(')');
        }

        Append(sb, results.Count(r => r.Verdict == UnitVerdict.OutOfScope), "poza zakresem korpusu");
        Append(sb, results.Count(r => r.Verdict == UnitVerdict.NoSources), "bez podstawy w źródłach");
        Append(sb, results.Count(r => r.Verdict == UnitVerdict.NoLegalContent), "bez treści prawnej");
        Append(sb, results.Count(r => r.Verdict is UnitVerdict.Error or UnitVerdict.Unknown), "z błędem lub bez wyniku");
        return sb.Append('.').ToString();
    }

    private static void Append(StringBuilder sb, int n, string label)
    {
        if (n > 0) sb.Append("; ").Append(n).Append(' ').Append(label);
    }

    // Po „z N": dopełniacz — „z 1 fragmentu", „z 14 fragmentów".
    private static string Fragments(int n) => n == 1 ? "fragmentu" : "fragmentów";
}
