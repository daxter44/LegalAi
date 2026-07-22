using System.Text.RegularExpressions;

namespace PrawoRAG.Api.Services;

/// <summary>Jednostka analizy dokumentu (SPK-1): logiczny fragment (§/art./pkt/akapit) z nagłówkiem
/// do wyświetlenia. <see cref="Text"/> zawiera nagłówek (czytelność w prompcie i w UI).</summary>
public sealed record DocUnit(int Index, string Heading, string Text);

/// <summary>
/// Dzieli tekst załącznika na JEDNOSTKI ANALIZY dla trybu „Analiza dokumentów" (spike) — inna oś niż
/// <see cref="DocChunker"/> (tam: równe porcje pod embedding; tu: jednostki LOGICZNE pod osobne
/// wywołania LLM). Strategie w kolejności: nagłówki §/art. na początku linii → nagłówki §/art.
/// w dowolnym miejscu (PdfPig często nie oddaje łamań linii — filtr odróżnia nagłówek od ODWOŁANIA
/// „zgodnie z § 5": numeracja nagłówków musi być kolejna, a odwołanie poprzedza przyimek) →
/// numerowane punkty na początku linii → akapity przez <see cref="DocChunker"/>. Czysty i deterministyczny.
/// </summary>
public static class LegalUnitSplitter
{
    /// <summary>Jednostki krótsze odpadają (nagłówki sekcji, podpisy, stopki) — nie ma czego analizować.</summary>
    public const int MinUnitChars = 60;

    /// <summary>Jednostka dłuższa jest dzielona na części „(cz. n)" — map-prompt musi zmieścić się
    /// w oknie lokalnego modelu (Bielik 4096 tok.) obok źródeł korpusu.</summary>
    public const int MaxUnitChars = 3500;

    /// <summary>Minimalna liczba nagłówków, żeby uznać strategię strukturalną za wiarygodną —
    /// poniżej to raczej przypadkowe dopasowania niż struktura dokumentu.</summary>
    public const int MinStructuralUnits = 3;

    private static readonly Regex LineStartRe = new(
        @"^[ \t]*(?<h>(?:§|[Aa]rt\.|[Aa]rtykuł)\s*(?<n>\d+)[a-z]?)\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AnywhereRe = new(
        @"(?<h>(?:§|[Aa]rt\.|[Aa]rtykuł)\s*(?<n>\d+)[a-z]?)(?=\s*\.?\s)",
        RegexOptions.Compiled);

    private static readonly Regex NumberedRe = new(
        @"^[ \t]*(?<h>(?<n>\d{1,3}))\.\s",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Tokeny poprzedzające ODWOŁANIE do jednostki (nie nagłówek): „zgodnie z § 5",
    /// „o którym mowa w § 3", „z zastrzeżeniem § 9". Porównywane bez wielkości liter.</summary>
    private static readonly string[] ReferencePrecedingTokens =
        ["w", "z", "ze", "i", "oraz", "lub", "albo", "a", "myśl", "mowa", "zastrzeżeniem",
         "według", "wg", "do", "od", "na", "zgodnie", "stosownie", "trybie", "rozumieniu"];

    public static IReadOnlyList<DocUnit> Split(IReadOnlyList<string> pages)
    {
        var text = string.Join("\n", pages).Trim();
        if (text.Length == 0) return [];

        // 1) Nagłówki na początku linii — najpewniejszy sygnał (tekst z zachowanym łamaniem).
        var cuts = LineStartRe.Matches(text).ToList();

        // 2) Tekst „płaski" (PdfPig bez łamań): nagłówki gdziekolwiek, z filtrem odwołań.
        if (cuts.Count < MinStructuralUnits)
            cuts = FilterReferences(AnywhereRe.Matches(text), text);

        if (cuts.Count >= MinStructuralUnits)
            return Finalize(BuildUnits(text, cuts, m => NormalizeHeading(m.Groups["h"].Value)));

        // 3) Numerowane punkty pisma („1. …", „2. …") — tylko na początku linii (w środku zdania
        //    to daty/kwoty, nie struktura).
        var numbered = NumberedRe.Matches(text).ToList();
        if (numbered.Count >= MinStructuralUnits)
            return Finalize(BuildUnits(text, numbered, m => $"pkt {m.Groups["n"].Value}"));

        // 4) Brak struktury — akapity przez DocChunker (równe porcje ≤1400 zn < MaxUnitChars).
        var fragments = DocChunker.Split(pages)
            .Select(c => c.Trim())
            .Where(c => c.Length >= MinUnitChars)
            .Select(c => new DocUnit(0, "", c))
            .ToList();
        if (fragments.Count == 0 && text.Length >= MinUnitChars)
            fragments = [new DocUnit(0, "", text)];
        return Finalize(fragments
            .Select((u, i) => u with { Heading = $"fragment {i + 1}" })
            .ToList());
    }

    /// <summary>Jednostki z pozycji nagłówków: tekst od nagłówka do następnego. Tekst PRZED pierwszym
    /// nagłówkiem (komparycja, tytuł) staje się jednostką „wstęp", jeśli jest dość długi.</summary>
    private static List<DocUnit> BuildUnits(string text, IReadOnlyList<Match> cuts, Func<Match, string> heading)
    {
        var units = new List<DocUnit>();

        var preamble = text[..cuts[0].Index].Trim();
        if (preamble.Length >= MinUnitChars)
            units.Add(new DocUnit(0, "wstęp", preamble));

        for (var i = 0; i < cuts.Count; i++)
        {
            var start = cuts[i].Index;
            var end = i + 1 < cuts.Count ? cuts[i + 1].Index : text.Length;
            var unitText = text[start..end].Trim();
            if (unitText.Length >= MinUnitChars)
                units.Add(new DocUnit(0, heading(cuts[i]), unitText));
        }
        return units;
    }

    /// <summary>Tnie za długie jednostki na części „(cz. n)" i nadaje ostateczne indeksy 1..N.</summary>
    private static IReadOnlyList<DocUnit> Finalize(List<DocUnit> units) =>
        units
            .SelectMany(u => u.Text.Length <= MaxUnitChars
                ? new[] { u }
                : SplitOversize(u.Text, MaxUnitChars)
                    .Select((p, k) => u with { Heading = $"{u.Heading} (cz. {k + 1})", Text = p }))
            .Select((u, i) => u with { Index = i + 1 })
            .ToList();

    /// <summary>Filtr trybu „płaskiego": nagłówki muszą mieć numerację KOLEJNĄ (1,2,3… — jak w realnej
    /// umowie), a dopasowanie poprzedzone przyimkiem/frazą odwołania odpada. Odwołanie wstecz („zgodnie
    /// z § 2" w § 5) odpada przez numerację; odwołanie w przód („z zastrzeżeniem § 9") — przez tokeny.</summary>
    private static List<Match> FilterReferences(MatchCollection matches, string text)
    {
        var kept = new List<Match>();
        var last = 0;
        foreach (Match m in matches)
        {
            var n = int.Parse(m.Groups["n"].Value);
            if (kept.Count > 0 && n != last + 1) continue;
            if (IsReference(text, m.Index)) continue;
            kept.Add(m);
            last = n;
        }
        return kept;
    }

    private static bool IsReference(string text, int matchIndex)
    {
        var windowStart = Math.Max(0, matchIndex - 30);
        var tokens = text[windowStart..matchIndex]
            .Split([' ', '\t', '\n', '(', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0
            && ReferencePrecedingTokens.Contains(tokens[^1].ToLowerInvariant());
    }

    private static string NormalizeHeading(string raw)
    {
        var h = Regex.Replace(raw, @"\s+", " ").Trim();
        return h.StartsWith('§') && !h.StartsWith("§ ") ? "§ " + h[1..].TrimStart() : h;
    }

    /// <summary>Jak <c>DocChunker.SplitOversize</c>: kawałki ≤ limit cięte na ostatniej spacji.</summary>
    private static IEnumerable<string> SplitOversize(string text, int maxChars)
    {
        while (text.Length > maxChars)
        {
            var cut = text.LastIndexOf(' ', maxChars - 1);
            if (cut <= 0) cut = maxChars;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
