using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Eval;

/// <summary>
/// T-SONDA — parser cytowań w stylu uzasadnień sądowych (most cytowań, diagnoza „statut
/// nieretrievalny" z 2026-07-17). Kluczowa własność: akt przypisywany TYLKO gdy skrót przylega
/// do numeru — jeden akapit orzeczenia cytuje wiele kodeksów i zgadywanie per-tekst (jak w
/// CitationParser dla pytań) dawałoby fałszywe przypisania.
/// </summary>
public class JudgmentCitationParserTests
{
    [Theory]
    [InlineData("art. 415 k.c.", "415", "KC")]
    [InlineData("art. 415 kc", "415", "KC")]
    [InlineData("art. 415 KC", "415", "KC")]
    [InlineData("Art. 233 k.p.c.", "233", "KPC")]
    [InlineData("art. 415 § 1 k.c.", "415", "KC")]           // § między numerem a skrótem
    [InlineData("art. 24 kodeksu cywilnego", "24", "KC")]     // pełna fraza w odmianie
    [InlineData("art. 98 kodeksu postępowania cywilnego", "98", "KPC")]
    [InlineData("art. 435 § 1 pkt 2 k.c.", "435", "KC")]      // § + pkt między numerem a skrótem
    [InlineData("art. 178a k.k.", "178a", "KK")]              // numer z literą
    public void Parses_article_with_adjacent_act(string text, string article, string alias)
    {
        var cite = Assert.Single(JudgmentCitationParser.Parse(text));
        Assert.Equal(article, cite.Article);
        Assert.Equal(alias, cite.Alias);
    }

    [Fact] // Sedno: mieszane cytowanie w JEDNYM zdaniu — każdy artykuł dostaje SWÓJ akt, nie pierwszy z tekstu
    public void Mixed_citation_attributes_each_article_to_its_own_act()
    {
        var cites = JudgmentCitationParser.Parse(
            "Na podstawie art. 415 k.c. w zw. z art. 361 § 1 k.c. oraz art. 98 k.p.c. sąd orzekł.");
        Assert.Equal(3, cites.Count);
        Assert.Equal(new JudgmentCitation("415", "KC"), cites[0]);
        Assert.Equal(new JudgmentCitation("361", "KC"), cites[1]);
        Assert.Equal(new JudgmentCitation("98", "KPC"), cites[2]);
    }

    [Fact] // Precyzja ponad recall: artykuł bez skrótu obok NIE jest przypisywany do aktu z dalszej części tekstu
    public void Article_without_adjacent_act_stays_unattributed()
    {
        var cites = JudgmentCitationParser.Parse("zgodnie z art. 6, a także art. 415 k.c.");
        Assert.Equal(2, cites.Count);
        Assert.Null(cites[0].Alias);            // art. 6 — nie zgadujemy, że to KC
        Assert.Equal("KC", cites[1].Alias);
    }

    [Fact] // k.p.c. nie może rozpaść się na k.p. + śmieć (dłuższe skróty mają pierwszeństwo)
    public void Longer_abbreviation_wins_over_prefix()
    {
        var cite = Assert.Single(JudgmentCitationParser.Parse("art. 100 k.p.c."));
        Assert.Equal("KPC", cite.Alias);
    }

    [Fact] // „kw" jako początek słowa („kwota") to nie Kodeks wykroczeń
    public void Abbreviation_glued_to_word_is_not_an_act()
    {
        var cite = Assert.Single(JudgmentCitationParser.Parse("art. 5 kwota zadośćuczynienia"));
        Assert.Null(cite.Alias);
    }

    [Fact] // Kodeks spoza mapy aliasów → świadomie nieprzypisany, nie błędnie zmapowany
    public void Unknown_code_phrase_stays_unattributed()
    {
        var cite = Assert.Single(JudgmentCitationParser.Parse("art. 57 kodeksu morskiego"));
        Assert.Null(cite.Alias);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wyrok nie zawiera cytowań przepisów")]
    public void No_citations_returns_empty(string text) =>
        Assert.Empty(JudgmentCitationParser.Parse(text));

    [Fact] // Realistyczny akapit uzasadnienia: agregacja per akt+artykuł jak w sondzie (sekcja D)
    public void Realistic_judgment_paragraph_yields_grouped_votes()
    {
        const string paragraph =
            "Podstawę odpowiedzialności pozwanego stanowi art. 415 k.c., zgodnie z którym kto z winy swej " +
            "wyrządził drugiemu szkodę, obowiązany jest do jej naprawienia. Związek przyczynowy ocenia się " +
            "według art. 361 § 1 k.c. O kosztach orzeczono na podstawie art. 98 § 1 k.p.c. i art. 108 k.p.c.";

        var votes = JudgmentCitationParser.Parse(paragraph)
            .Where(c => c.Alias is not null)
            .GroupBy(c => (c.Alias, c.Article))
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(1, votes[("KC", "415")]);
        Assert.Equal(1, votes[("KC", "361")]);
        Assert.Equal(1, votes[("KPC", "98")]);
        Assert.Equal(1, votes[("KPC", "108")]);
    }
}
