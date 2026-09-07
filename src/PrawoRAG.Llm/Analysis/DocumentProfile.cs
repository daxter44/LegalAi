using System.Text;
using System.Text.RegularExpressions;

namespace PrawoRAG.Llm.Analysis;

/// <summary>
/// Profil dokumentu (AJ-3): FAKTY z całości załącznika, ustalane raz per dokument i doklejane do
/// promptu każdej jednostki fazy map (AJ-4). Rozwiązuje lukę A z przeglądu 2026-09-02: fragment
/// oceniany w izolacji nie wie, czy to najem mieszkania czy B2B, kto jest konsumentem, co
/// zdefiniowano w § 1. Profil idzie WYŁĄCZNIE do promptu modelu; do zapytania retrievalu nie wchodzi
/// (zmierzone 2026-09-03: typ dokumentu + strony w zapytaniu nie poprawiały trafienia normy).
/// Profil NIE jest persystowany (D1) — żyje w sesji jak treść §. Zero ocen prawnych — patrz
/// <see cref="DocumentProfilePrompts.IsClean"/>.
/// </summary>
public sealed record DocumentProfile(
    string? Kind,
    string? Parties,
    string? Subject,
    string? Definitions,
    string? CitedActs,
    string? CitedJudgments)
{
    public bool IsEmpty =>
        Kind is null && Parties is null && Subject is null && Definitions is null && CitedActs is null && CitedJudgments is null;

    /// <summary>Blok do promptu fazy map — wyłącznie fakty, etykiety po polsku, puste pola pomijane.</summary>
    public string ToPromptBlock()
    {
        var sb = new StringBuilder();
        Append(sb, "Rodzaj dokumentu", Kind);
        Append(sb, "Strony", Parties);
        Append(sb, "Przedmiot", Subject);
        Append(sb, "Definicje z części ogólnej", Definitions);
        Append(sb, "Powołane akty", CitedActs);
        Append(sb, "Powołane orzeczenia", CitedJudgments);
        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.Append(label).Append(": ").Append(value.Trim()).Append('\n');
    }
}

/// <summary>Prompt, parser i strażnik profilu — czyste funkcje, testowalne bez LLM.</summary>
public static class DocumentProfilePrompts
{
    /// <summary>Budżet próbki dokumentu (znaki) — wstęp + kolejne jednostki do wypełnienia.</summary>
    public const int SampleChars = 3000;

    public const string SystemPrompt =
        """
        Jesteś asystentem prawnika. Dostajesz początek dokumentu (umowa, regulamin, pismo, decyzja).
        Wypisz WYŁĄCZNIE FAKTY zapisane w tekście, w formacie liniowym — każda linia zaczyna się od
        jednej z etykiet:
        TYP: rodzaj dokumentu jednym zdaniem (np. „umowa najmu lokalu mieszkalnego na czas oznaczony")
        STRONY: kto jest stroną i w jakiej roli; przy każdej stronie zaznacz status, jeśli wynika z tekstu
          (osoba fizyczna / konsument / przedsiębiorca / organ administracji)
        PRZEDMIOT: czego dokument dotyczy (przedmiot umowy, żądanie wniosku, rozstrzygnięcie)
        DEFINICJE: pojęcia zdefiniowane w części ogólnej, po przecinku; pomiń linię, jeśli brak
        AKTY: akty prawne powołane w tekście, po przecinku; pomiń linię, jeśli brak
        ORZECZENIA: orzeczenia powołane w tekście (sąd, data, sygnatura), po przecinku; pomiń, jeśli brak
        Zasady bezwzględne: NIE oceniaj zgodności z prawem, NIE używaj słów „narusza", „niezgodne",
        „nieważne", „abuzywne", NIE dodawaj przepisów ani orzeczeń, których nie ma w tekście, NIE używaj
        znaczników [n]. Jeśli czegoś nie ma w tekście, pomiń linię. Maksymalnie 6 linii, bez komentarzy.
        """;

    /// <summary>Próbka: „wstęp" + kolejne jednostki w kolejności dokumentu do budżetu znaków.
    /// Pierwsza jednostka zawsze wchodzi (nawet ucięta), żeby profil miał komparycję.</summary>
    public static string BuildSample(IReadOnlyList<DocUnit> units, int budget = SampleChars)
    {
        var sb = new StringBuilder();
        foreach (var u in units)
        {
            if (sb.Length == 0)
            {
                sb.Append(u.Text.Length <= budget ? u.Text : u.Text[..budget]).Append('\n');
                continue;
            }
            if (sb.Length + u.Text.Length + 1 > budget) break;
            sb.Append(u.Text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public static string UserInput(string sample) => $"Początek dokumentu:\n---\n{sample}\n---";

    private static readonly Regex LineRe = new(
        // Po dwukropku TYLKO poziome białe znaki — `\s*` zjadałoby newline i „TYP:\nSTRONY: -" dawałoby
        // Kind = „STRONY: -". Gwiazdki markdownu dopuszczone po obu stronach dwukropka („**Typ:**").
        @"^\s*\**\s*(?<k>TYP|STRONY|PRZEDMIOT|DEFINICJE|AKTY|ORZECZENIA)\s*\**\s*:\**[ \t]*(?<v>[^\r\n]+?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parser liniowy (wzorzec KAZ): nieznane linie ignorowane, powtórzona etykieta —
    /// pierwsza wygrywa. Null = ani jednej rozpoznanej linii (analiza działa jak bez profilu).</summary>
    public static DocumentProfile? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LineRe.Matches(text))
        {
            var k = m.Groups["k"].Value.ToUpperInvariant();
            var v = m.Groups["v"].Value.Trim().TrimEnd('.');
            if (v.Length == 0 || IsPlaceholder(v)) continue;
            fields.TryAdd(k, v);
        }
        if (fields.Count == 0) return null;
        return new DocumentProfile(
            fields.GetValueOrDefault("TYP"),
            fields.GetValueOrDefault("STRONY"),
            fields.GetValueOrDefault("PRZEDMIOT"),
            fields.GetValueOrDefault("DEFINICJE"),
            fields.GetValueOrDefault("AKTY"),
            fields.GetValueOrDefault("ORZECZENIA"));
    }

    private static bool IsPlaceholder(string v) =>
        v.Equals("brak", StringComparison.OrdinalIgnoreCase) || v.Equals("-", StringComparison.Ordinal) ||
        v.Equals("—", StringComparison.Ordinal) || v.Equals("nie dotyczy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Słowa oceny prawnej — profil z nimi jest odrzucany w całości (fail-safe do dzisiejszego
    /// zachowania). Powód: profil trafia do KAŻDEGO promptu map; jedna ocena w profilu zaraziłaby
    /// niezależne werdykty wszystkich jednostek (uwaga z raportu 07-23).</summary>
    private static readonly Regex AssessmentRe = new(
        @"narusz|niezgodn|nieważn|niewazn|abuzywn|sprzeczn\w*\s+z\s+(prawem|ustaw)|bezskuteczn|niedozwolon|klauzul\w*\s+niedozwolon|\[\d+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsClean(DocumentProfile profile) =>
        !AssessmentRe.IsMatch(profile.ToPromptBlock());

    /// <summary>Parse + strażnik w jednym: zwraca profil tylko, gdy jest czysty i niepusty.</summary>
    public static DocumentProfile? ParseClean(string? text) =>
        Parse(text) is { IsEmpty: false } p && IsClean(p) ? p : null;
}
