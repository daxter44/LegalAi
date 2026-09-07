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
        Regex.IsMatch(text, ArticlePattern(article), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && DiffVerbRe.IsMatch(text);

    /// <summary>
    /// Buduje wzorzec numeru artykułu dopuszczający DWA zapisy artykułu z indeksem górnym (np. „art. 35¹"):
    /// podkreślnik, jak w <c>Locator.Article</c>/kolumnie <c>ArticleNo</c> ("35_1"), i nawiasy kwadratowe,
    /// jak w surowym tekście nowel z ISAP ("35[1]") — to ta sama jednostka redakcyjna zapisana przez dwa
    /// różne parsery źródłowe. Bez tego dopasowania <see cref="MentionsArticleChange"/> nigdy nie łapał
    /// nowel zmieniających artykuły z indeksem: zmierzone 2026-08-22, DU/2026/731 zmienia art. 35[1] ust. 5
    /// ustawy o radcach prawnych (w mocy od 18.06.2026, poprawnie oznaczone jako niewchłonięta nowela w
    /// metadanych aktu bazowego), ale TemporalAugmenter nigdy tego nie dołożył, bo `35_1` (Locator.Article)
    /// nie pasował tekstowo do `35[1]` (zapis w treści noweli) — regex szukał dosłownie podkreślnika.
    /// Artykuły BEZ indeksu górnego (np. "48") i z literą (np. "10c") nie mają podkreślnika — przechodzą
    /// niezmienione, zero wpływu na dotychczasowe dopasowania.
    /// </summary>
    private static string ArticlePattern(string article)
    {
        var sep = article.IndexOf('_');
        if (sep < 0) return @"\bart\.?\s*" + Regex.Escape(article) + @"\b";

        var basePart = Regex.Escape(article[..sep]);
        var indexPart = Regex.Escape(article[(sep + 1)..]);
        return @"\bart\.?\s*" + basePart + "_" + indexPart + @"\b"
             + "|"
             + @"\bart\.?\s*" + basePart + @"\[" + indexPart + @"\]";
    }
}
