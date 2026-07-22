using System.Text;
using System.Text.RegularExpressions;
using PrawoRAG.Domain.Llm;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Dopytania po raporcie analizy (SPK-6) — czyste funkcje. Problem: cały raport + dokument nie
/// zmieści się w oknie lokalnego modelu, a „wszystko naraz" rozmywa retrieval. Rozwiązanie:
/// selekcja kontekstu PER PYTANIE — (1) routing mechaniczny po odwołaniach („a co z § 7?"),
/// (2) fallback embeddingowy (cosine pytanie ↔ jednostki, embeddingi policzone raz w fazie map),
/// (3) pytanie przekrojowe → sama tabela werdyktów. Wybrany kontekst wchodzi jako TURA-KOTWICA
/// (<see cref="ChatTurn"/>) do zwykłego ChatService — reużycie istniejącego mechanizmu follow-upów
/// (fold odpowiedzi + kotwice źródeł), retrieval korpusu działa dalej (dopytanie może wymagać
/// świeżego prawa, którego nie było w analizie).
/// </summary>
public static class AnalysisFollowUp
{
    /// <summary>Ile jednostek wchodzi do kontekstu dopytania (budżet
    /// <see cref="PrawoRAG.Llm.Grounding.GroundedPrompt.MaxHistoryAnswerChars"/> = 1500 znaków
    /// na całą kotwicę).</summary>
    public const int MaxUnitsInContext = 2;

    /// <summary>Odwołanie do jednostki w pytaniu: „§ 7", „paragraf 7", „art. 3", „pkt 2".</summary>
    private static readonly Regex ReferenceRe = new(
        @"(?<kind>§|paragraf\w*|art\.?|artykuł\w*|pkt|punkt\w*|fragment\w*)\s*(?<n>\d+[a-z]?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Routing mechaniczny: indeksy jednostek, do których pytanie odwołuje się WPROST.
    /// Nagłówek „§ 2 (cz. 1)" pasuje do odwołania „§ 2" (wszystkie części wchodzą). Pusta lista =
    /// brak odwołań (użyj fallbacku embeddingowego albo trybu przekrojowego).</summary>
    public static IReadOnlyList<int> FindReferencedUnits(string question, IReadOnlyList<DocUnit> units)
    {
        var refs = ReferenceRe.Matches(question)
            .Select(m => (Kind: NormalizeKind(m.Groups["kind"].Value), N: m.Groups["n"].Value.ToLowerInvariant()))
            .ToHashSet();
        if (refs.Count == 0) return [];

        return units
            .Where(u => ReferenceRe.Match(u.Heading) is { Success: true } h
                && refs.Contains((NormalizeKind(h.Groups["kind"].Value), h.Groups["n"].Value.ToLowerInvariant())))
            .Select(u => u.Index)
            .ToList();
    }

    /// <summary>Fallback embeddingowy: top-K jednostek po cosine do pytania, zwracane w KOLEJNOŚCI
    /// DOKUMENTU (jak <see cref="DocumentContext.SelectFragments"/> — ranking służy tylko selekcji).</summary>
    public static IReadOnlyList<int> SelectByEmbedding(
        float[] queryEmbedding, IReadOnlyList<float[]> unitEmbeddings, int topK = MaxUnitsInContext)
    {
        if (unitEmbeddings.Count == 0 || topK <= 0) return [];
        return unitEmbeddings
            .Select((e, i) => (Index: i + 1, Sim: Cosine(queryEmbedding, e)))
            .OrderByDescending(x => x.Sim)
            .Take(topK)
            .Select(x => x.Index)
            .OrderBy(i => i)
            .ToList();
    }

    /// <summary>
    /// Tura-kotwica: raport analizy udaje „poprzednią odpowiedź asystenta". Kolejność treści wg
    /// ważności (GroundedPrompt tnie OGON do 1500 znaków — to, co najważniejsze dla pytania, musi
    /// być z przodu): wybrane jednostki (treść + werdykt + uzasadnienie) → streszczenie → tabela
    /// werdyktów wszystkich jednostek (pytania przekrojowe). SourceAnchors = etykiety źródeł
    /// wybranych jednostek (kontekstualizacja retrievalu follow-upu).
    /// </summary>
    public static ChatTurn ComposeAnchorTurn(AnalysisSnapshot snap, IReadOnlyList<int> selectedIndexes)
    {
        var selected = selectedIndexes
            .Take(MaxUnitsInContext)
            .Select(i => (Unit: snap.Units[i - 1], Result: snap.Results[i - 1]))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("Przeanalizowałem dokument „").Append(snap.FileName).Append("” fragment po fragmencie.\n");

        foreach (var (unit, result) in selected)
        {
            sb.Append('\n').Append(unit.Heading);
            if (result is not null)
                sb.Append(" — ").Append(AnalysisPrompts.Label(result.Verdict)).Append(": ")
                  .Append(Trim(result.Answer ?? result.Error ?? "", 260));
            sb.Append("\nTreść: ").Append(Trim(unit.Text, 240)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(snap.Summary))
            sb.Append("\nStreszczenie: ").Append(snap.Summary).Append('\n');

        sb.Append("\nWerdykty: ").Append(string.Join("; ", snap.Results
            .Where(r => r is not null)
            .Select(r => $"{r!.Heading} — {AnalysisPrompts.Label(r.Verdict)}")));

        var anchors = selected
            .Where(s => s.Result is not null)
            .SelectMany(s => s.Result!.Sources)
            .Select(src => src.Label)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .Take(6)
            .ToList();

        var answer = sb.ToString();
        if (answer.Length > PrawoRAG.Llm.Grounding.GroundedPrompt.MaxHistoryAnswerChars)
            answer = answer[..PrawoRAG.Llm.Grounding.GroundedPrompt.MaxHistoryAnswerChars] + "…";

        return new ChatTurn(snap.Prompt, answer, anchors.Count > 0 ? anchors : null);
    }

    private static string NormalizeKind(string raw) => raw.ToLowerInvariant() switch
    {
        "§" => "§",
        var k when k.StartsWith("paragraf") => "§",
        var k when k.StartsWith("art") => "art",
        var k when k.StartsWith("pkt") => "pkt",
        var k when k.StartsWith("punkt") => "pkt",
        var k when k.StartsWith("fragment") => "fragment",
        _ => raw.ToLowerInvariant(),
    };

    private static string Trim(string text, int max)
    {
        var clean = Regex.Replace(text, @"\s+", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException(
                $"Niezgodność wymiarów embeddingów ({a.Length} vs {b.Length}) — pytanie i jednostki muszą iść przez ten sam model.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
