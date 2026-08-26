using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-UE-4 — rozpoznanie cytatu z prawa UE w pytaniu („art. 6 ust. 1 lit. f) RODO",
/// „rozporządzenie (UE) 2016/679", „dyrektywa 95/46/WE"). Bez tego pytanie cytujące przepis unijny
/// nie ma toru exact-match i idzie samym wektorem.
///
/// Druga połowa testów to RÓWNOWAŻNOŚĆ dla prawa polskiego: dodanie „ust." do wzorca jednostki
/// i skrótów UE nie może zmienić rozpoznania „art. 148 § 2 KK" ani wciągnąć słowa „ustawa" jako
/// numeru ustępu. To jedyne miejsce w całym planie prawa UE, gdzie ruszamy kod wspólny z korpusem
/// polskim, więc regresja tutaj uderza w to, co już zmierzone.
/// </summary>
public class CitationParserEuTests
{
    [Fact]
    public void Recognizes_eu_alias_with_article_and_paragraph()
    {
        var c = Assert.Single(CitationParser.Parse("Czy art. 6 ust. 1 lit. f) RODO pozwala na marketing?"));

        Assert.Equal("6", c.Article);
        Assert.Equal("1", c.Paragraph);
        Assert.Equal("RODO", c.ActHint);
        Assert.Equal("2016/679", ActAliases.Canonical(c.ActHint)); // fragment tytułu aktu z CELLAR-a
    }

    [Fact] // Użytkownik pisze różnie („AI act", „ai Act"), a mapa aliasów ma jedno brzmienie.
    public void Recognizes_ai_act_alias_regardless_of_case()
    {
        Assert.Equal("AI Act", CitationParser.Parse("Co zakazuje art. 5 AI Act?").Single().ActHint);
        Assert.Equal("AI Act", CitationParser.Parse("art. 50 ai act – obowiązki").Single().ActHint);
        Assert.Equal("2024/1689", ActAliases.Canonical("AI Act"));
    }

    [Fact] // Oznaczenie podane wprost jest dokładniejsze niż nazwa zwyczajowa — ma pierwszeństwo.
    public void Recognizes_explicit_designator()
    {
        Assert.Equal("2016/679", CitationParser.Parse("art. 28 rozporządzenia (UE) 2016/679").Single().ActHint);
        Assert.Equal("45/2001", CitationParser.Parse("art. 3 rozporządzenia (WE) nr 45/2001").Single().ActHint);
        Assert.Equal("95/46/WE", CitationParser.Parse("art. 7 dyrektywy 95/46/WE").Single().ActHint);
    }

    [Fact] // Oznaczenie aktu UE jest już kanonicznym fragmentem tytułu (przechodzi bez mapowania).
    public void Designator_passes_through_aliases()
    {
        Assert.Equal("2022/2065", ActAliases.Canonical("2022/2065"));
        Assert.Equal("95/46/WE", ActAliases.Canonical("95/46/WE"));
        Assert.Null(ActAliases.Canonical("jakaś fraza"));
    }

    [Fact] // Zamknięcie toru: alias z pytania musi trafić w TYTUŁ dokumentu, który realnie wchodzi
           // do korpusu z CELLAR-a. Inaczej rozpoznanie „RODO" nie znajdzie aktu w bazie.
    public void Alias_matches_title_of_real_normalized_document()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            celex = "32016R0679", textCelex = "02016R0679-20160504", textVersion = "consolidated",
            actClass = "substantive", language = "pol",
        });
        var doc = new EuActNormalizer().Normalize(new RawDocument
        {
            Source = SourceKeys.EurLex,
            ExternalId = "32016R0679",
            DocType = DocTypes.EuAct,
            RawContent = EurLexFixtures.Read(EurLexFixtures.RodoConsolidated),
            ContentFormat = ContentFormats.Html,
            SourcePayload = payload,
        });

        var canonical = ActAliases.Canonical(CitationParser.Parse("art. 6 RODO").Single().ActHint);

        Assert.NotNull(canonical);
        Assert.Contains(canonical, doc.Title);                       // dopasowanie po tytule zadziała
        Assert.Equal("32016R0679", doc.Locator?.EliId);              // exact-match po akcie = CELEX
        Assert.Contains(doc.Segments, s => s.Locator?.Article == "6"); // …i po numerze artykułu
    }

    // --- równoważność: prawo polskie bez zmian ---

    [Fact]
    public void Polish_code_citation_unchanged()
    {
        var c = Assert.Single(CitationParser.Parse("Jaka kara za art. 148 § 2 KK?"));

        Assert.Equal("148", c.Article);
        Assert.Equal("2", c.Paragraph);
        Assert.Equal("KK", c.ActHint);
    }

    [Fact] // „ustawa o …" nie może zostać zinterpretowana jako „ust." + numer ani przegrać z aliasem UE.
    public void Ustawa_phrase_is_not_parsed_as_paragraph()
    {
        var c = Assert.Single(CitationParser.Parse("art. 15 ustawy o ochronie zwierząt"));

        Assert.Equal("15", c.Article);
        Assert.Null(c.Paragraph);
        Assert.Contains("ochronie zwierząt", c.ActHint);
    }

    [Fact] // Pytanie o polską ustawę o ochronie danych NIE może przeskoczyć na RODO tylko przez temat.
    public void Polish_data_protection_act_is_not_hijacked_by_eu_alias()
        => Assert.Contains("ochronie danych",
            CitationParser.Parse("art. 34 ustawy o ochronie danych osobowych").Single().ActHint);

    [Fact] // Skrót jako fragment słowa to nie cytat UE („dsa" w środku wyrazu).
    public void Does_not_match_alias_inside_word()
        => Assert.Null(CitationParser.Parse("art. 5 dsawcy nie istnieje").Single().ActHint);

    [Fact] // Kodeksy nadal wygrywają z prawem UE, gdy pytanie mówi o kodeksie.
    public void Codex_phrase_still_wins()
        => Assert.Contains("odeks", CitationParser.Parse("art. 148 kodeksu karnego a RODO").Single().ActHint);
}
