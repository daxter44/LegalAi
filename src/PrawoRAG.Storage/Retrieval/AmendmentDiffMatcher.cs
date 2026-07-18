using System.Text.RegularExpressions;

namespace PrawoRAG.Storage.Retrieval;

/// <summary>
/// Kwalifikacja fragmentu noweli do augmentacji (AKT-2, zaostrzone po raporcie odmów 2026-07-18):
/// kontrakt brzmi „fragmenty nowel DOTYCZĄCE pytanych artykułów" — czyli fragmenty ZMIENIAJĄCE przepis,
/// nie wzmiankujące go. Sama wzmianka `\bart\. N\b` łapała: nagłówek własnego artykułu noweli
/// („Art. 43. Związek metropolitalny…") i zwykłe odesłania („o którym mowa w art. 10c ustawy…") —
/// szeroka ustawa zmieniająca (np. o związku metropolitalnym 2026) stawała się „atraktorem"
/// zaśmiecającym źródła niezwiązanych pytań (Case 1 i 3 raportu). Polski diff legislacyjny ma
/// rozpoznawalny język — wymagamy jego obecności w chunku obok wzmianki artykułu.
/// </summary>
public static class AmendmentDiffMatcher
{
    /// <summary>Czasowniki nowelizacyjne z techniki prawodawczej (ZTP): „w art. X … otrzymuje brzmienie",
    /// „po art. X dodaje się", „uchyla się", „skreśla się", „zastępuje się wyrazami".</summary>
    private static readonly Regex DiffVerbRe = new(
        @"otrzymuj[eą]\s+brzmienie|dodaje\s+się|uchyla\s+się|skreśla\s+się|zastępuje\s+się|wprowadza\s+się\s+następując",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Czy chunk noweli ZMIENIA artykuł <paramref name="article"/>: wzmianka numeru + język diffu.
    /// Chunk jest mały (≤ ~450 tokenów), więc współwystępowanie w chunku ≈ współwystępowanie w przepisie
    /// zmieniającym — bez analizy odległości.</summary>
    public static bool MentionsArticleChange(string text, string article) =>
        Regex.IsMatch(text, @"\bart\.?\s*" + Regex.Escape(article) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && DiffVerbRe.IsMatch(text);
}
