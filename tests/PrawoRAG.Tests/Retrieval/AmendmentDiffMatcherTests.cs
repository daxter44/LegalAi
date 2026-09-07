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

    // Regresja 2026-08-22: DU/2026/731 zmienia art. 35[1] ust. 5 ustawy o radcach prawnych (w mocy od
    // 18.06.2026), ale Locator.Article dla bazowego aktu niesie "35_1" (podkreślnik) — dwa różne zapisy
    // TEGO SAMEGO artykułu z indeksem górnym z dwóch różnych parserów źródłowych. Bez normalizacji
    // dopasowanie nigdy nie zachodziło i nowela nigdy nie trafiała do augmentacji.
    [Theory]
    [InlineData("w art. 35[1] ust. 5 otrzymuje brzmienie: „5. Aplikant adwokacki…”", "35_1")] // nawias (ISAP)
    [InlineData("w art. 35_1 ust. 5 otrzymuje brzmienie: „5. Aplikant adwokacki…”", "35_1")]   // podkreślnik (Locator.Article)
    [InlineData("9) w art. 22[8] po wyrazach „art. 22[7]” dodaje się wyrazy „ust. 1”", "22_8")] // wiele indeksów w jednym chunku
    public void Superscript_article_matches_both_underscore_and_bracket_notation(string text, string article) =>
        Assert.True(AmendmentDiffMatcher.MentionsArticleChange(text, article));

    [Fact] // sama wzmianka nawiasowego indeksu bez języka diffu dalej NIE kwalifikuje (odesłanie, nie zmiana)
    public void Bracket_notation_mere_mention_does_not_qualify() =>
        Assert.False(AmendmentDiffMatcher.MentionsArticleChange("zgodnie z art. 35[1] stosuje się odpowiednio", "35_1"));

    [Fact] // nawiasowy zapis INNEGO indeksu tego samego artykułu bazowego nie kwalifikuje (22[7] ≠ 22_8)
    public void Bracket_notation_different_index_does_not_qualify() =>
        Assert.False(AmendmentDiffMatcher.MentionsArticleChange("w art. 22[7] dodaje się ust. 3", "22_8"));

    // Dokładny tekst chunku 8 z DU/2026/731 (korpus, 2026-08-22) — jeden chunk zmienia CZTERY różne
    // artykuły z indeksem górnym naraz (22[8], 22[7] jako odesłanie, 22[10], 35[1]). Sprawdza, że
    // dopasowanie trafia właściwy artykuł i NIE łapie sąsiednich indeksów tego samego chunku.
    private const string RealAmendingChunk8 = "umowy ubezpieczenia przez radców praw-nych, którzy złożyli oświadczenie, o którym mowa w ust. 7. Spełnienie tego obowiązku ustala się na podstawie okaza-nej przez radcę prawnego polisy lub innego dokumentu ubezpieczenia, potwierdzającego zawarcie umowy ubezpie-czenia, wystawionego przez zakład ubezpieczeń. 11. Minister Sprawiedliwości nadzoruje wykonywanie przez rady okręgowe izb radców prawnych zadań okreś-lonych w ust. 10. Dziekani rad okręgowych izb radców prawnych obowiązani są do składania Ministrowi Sprawiedli-wości raz w roku, w terminie do dnia 15 marca, sprawozdań z kontroli przeprowadzonych w poprzednim roku kalen-darzowym.”; 9) w art. 22[8] po wyrazach „art. 22[7]” dodaje się wyrazy „ust. 1”; 10) w art. 22[10] w ust. 2 wyrazy „art. 28 ust. 1 pkt 2 i 3 i ust. 2” zastępuje się wyrazami „art. 28 ust. 1 pkt 2 i 3, ust. 1a i 2”; 11) w art. 28 po ust. 1 dodaje się ust. 1a w brzmieniu: „1a. Zawieszenie prawa do wykonywania zawodu radcy prawnego następuje także z chwilą uprawomocnienia się uchwały stwierdzającej niezdolność radcy prawnego do wykonywania zawodu lub nadania jej rygoru natychmia-stowej wykonalności.”; 12) w art. 35[1] ust. 5 otrzymuje brzmienie: „5. Aplikant adwokacki może zastępować radcę prawnego na takich samych zasadach jak aplikant radcowski.”; 13) w art. 37 w ust. 1 pkt 3 otrzymuje brzmienie: „3) uzyskania prawa wykonywania zawodu radcy prawnego zgodnie z art. 23;”; 14) art. 45 otrzymuje brzmienie: „";

    [Theory]
    [InlineData("35_1", true)]  // art. 35[1] ust. 5 — przypadek, który zdiagnozował ten regres
    [InlineData("22_8", true)]  // 9) w art. 22[8] … dodaje się wyrazy
    [InlineData("22_10", true)] // 10) w art. 22[10] w ust. 2 … zastępuje się
    [InlineData("22_7", true)]  // cytowany wewnątrz zmiany innego artykułu (22[8]) — dopasowanie jest
                                 // na poziomie CAŁEGO chunku (bez analizy odległości, jak w klasowym
                                 // komentarzu), więc to znany, akceptowany false-positive tego mechanizmu,
                                 // nie regresja wprowadzona tą poprawką (dotyczy notacji, nie precyzji)
    [InlineData("28", true)]    // 11) w art. 28 po ust. 1 dodaje się ust. 1a
    [InlineData("37", true)]    // 13) w art. 37 w ust. 1 pkt 3 otrzymuje brzmienie
    [InlineData("45", true)]    // 14) art. 45 otrzymuje brzmienie
    [InlineData("35_2", false)] // indeks, którego w tym chunku w ogóle nie ma
    public void Real_corpus_chunk_with_multiple_superscript_articles(string article, bool expected) =>
        Assert.Equal(expected, AmendmentDiffMatcher.MentionsArticleChange(RealAmendingChunk8, article));
}
