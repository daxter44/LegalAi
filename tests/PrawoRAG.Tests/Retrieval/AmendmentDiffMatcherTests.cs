using PrawoRAG.Storage.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-AKT-DIFF — zaostrzona kwalifikacja fragmentów nowel (raport odmów 2026-07-18): fragment
/// wchodzi do augmentacji tylko gdy ZMIENIA pytany artykuł (wzmianka numeru + język diffu ZTP),
/// nie gdy go wzmiankuje. Blokuje „atraktora": szeroką ustawę zmieniającą, której zwykłe odesłania
/// („o którym mowa w art. 10c ustawy…") i nagłówki własnych artykułów zaśmiecały źródła
/// niezwiązanych pytań (Case 1 i 3 raportu).
/// </summary>
public class AmendmentDiffMatcherTests
{
    [Theory] // realny język diffu legislacyjnego → kwalifikuje się
    [InlineData("w art. 43 ust. 1 otrzymuje brzmienie: „Nieruchomości mogą być…”", "43")]
    [InlineData("po art. 631 dodaje się art. 632 w brzmieniu:", "631")]
    [InlineData("w art. 10c uchyla się ust. 2;", "10c")]
    [InlineData("W ART. 48 SKREŚLA SIĘ pkt 3", "48")] // wielkość liter bez znaczenia
    public void Diff_language_qualifies(string text, string article) =>
        Assert.True(AmendmentDiffMatcher.MentionsArticleChange(text, article));

    [Theory] // wzmianka bez języka diffu → NIE kwalifikuje się (dokładnie wzorce z raportu)
    [InlineData("zadania, o których mowa w art. 10c ustawy o samorządzie gminnym, wykonuje związek", "10c")] // odesłanie
    [InlineData("Art. 43. Związek metropolitalny może tworzyć jednostki organizacyjne.", "43")]              // nagłówek własny
    [InlineData("zgodnie z art. 48 stosuje się odpowiednio", "48")]                                          // przywołanie
    public void Mere_mention_does_not_qualify(string text, string article) =>
        Assert.False(AmendmentDiffMatcher.MentionsArticleChange(text, article));

    [Fact] // język diffu jest, ale INNY artykuł → nie kwalifikuje się dla pytanego
    public void Diff_of_other_article_does_not_qualify()
        => Assert.False(AmendmentDiffMatcher.MentionsArticleChange("w art. 99 otrzymuje brzmienie:", "43"));

    [Fact] // numer z literą nie łapie samego prefiksu ("art. 10" ≠ "art. 10c" i odwrotnie)
    public void Article_number_matched_exactly()
    {
        Assert.False(AmendmentDiffMatcher.MentionsArticleChange("w art. 10c dodaje się ust. 3", "10"));
        Assert.True(AmendmentDiffMatcher.MentionsArticleChange("w art. 10c dodaje się ust. 3", "10c"));
    }
}
