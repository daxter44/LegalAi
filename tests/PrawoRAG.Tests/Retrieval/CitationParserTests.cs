using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Ekstraktor cytatów (QU-0) — czysty, bez sieci/DB. Odporność na warianty zapisu artykułu i aktu
/// (skróty, brak kropek, polska odmiana), wyliczenia, oraz brak fałszywych trafień w pytaniach pojęciowych.
/// </summary>
public class CitationParserTests
{
    [Fact] // realne pytanie z M4, które poległo w retrievalu
    public void Parses_real_m4_question()
    {
        var refs = CitationParser.Parse("a co z Art. 94 § 2 w zw. z § 1 Kodeksu wykroczeń ?");
        var r = Assert.Single(refs);
        Assert.Equal("94", r.Article);
        Assert.Equal("2", r.Paragraph);            // pierwszy § (informacyjnie; pobieramy cały art.)
        Assert.Contains("wykroczeń", r.ActHint);   // fraza aktu wyłuskana
    }

    [Fact] // pytanie pojęciowe — NIE może dawać fałszywego cytatu
    public void No_citation_in_conceptual_question()
        => Assert.Empty(CitationParser.Parse("jaka jest kara za jazdę samochodem bez ważnego przeglądu"));

    [Theory]
    [InlineData("art. 148 kodeksu karnego", "148")]
    [InlineData("Art.32 KK", "32")]
    [InlineData("co mówi artykuł 190a", "190a")]     // numer z literą
    [InlineData("przepis art 5 k.p.c.", "5")]        // brak kropki po art + skrót z kropkami
    public void Extracts_article_number_variants(string q, string expected)
        => Assert.Equal(expected, CitationParser.Parse(q)[0].Article);

    [Theory]
    [InlineData("art. 148 kodeksu karnego", "karnego")]
    [InlineData("co mówi art 32 KK", "KK")]
    [InlineData("art. 5 k.p.c.", "KPC")]
    [InlineData("art. 94 kodeksu wykroczeń", "wykroczeń")]
    public void Recognizes_act_hint(string q, string hintContains)
        => Assert.Contains(hintContains, CitationParser.Parse(q)[0].ActHint);

    [Fact] // wyliczenie: „art. 94 i 95" → dwa cytaty, wspólny akt
    public void Parses_enumerated_articles()
    {
        var refs = CitationParser.Parse("art. 94 i 95 kodeksu wykroczeń");
        Assert.Equal(2, refs.Count);
        Assert.Equal("94", refs[0].Article);
        Assert.Equal("95", refs[1].Article);
        Assert.All(refs, r => Assert.Contains("wykroczeń", r.ActHint));
    }

    [Fact] // skrót nie może wyłapać się WEWNĄTRZ słowa ani pomylić KP z KPC
    public void Abbrev_boundaries_are_respected()
    {
        Assert.Equal("KPC", CitationParser.Parse("art. 1 kpc")[0].ActHint);       // nie „KP"
        Assert.Null(CitationParser.Parse("art. 5 tej ustawy")[0].ActHint);        // brak skrótu/frazy
    }
}
