using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-DOCBODY — składanie treści dokumentu z chunków (czysta funkcja): scalanie sekcji, przycinanie
/// pokrywających się linii na granicy chunków (zakładka), czytelne nagłówki sekcji orzeczeń.
/// </summary>
public class DocumentBodyTests
{
    [Fact] // chunki tej samej sekcji z zakładką → jedna sekcja bez zdublowanych linii
    public void Merges_same_section_and_trims_line_overlap()
    {
        var body = DocumentBody.Assemble(
        [
            ("uzasadnienie", "Linia A\nLinia B\nLinia C"),
            ("uzasadnienie", "Linia B\nLinia C\nLinia D"), // B,C to zakładka
        ]);

        var s = Assert.Single(body);
        Assert.Equal("Uzasadnienie", s.Label);
        Assert.Equal("Linia A\nLinia B\nLinia C\nLinia D", s.Text);
    }

    [Fact] // różne sekcje → osobne bloki, w kolejności, z czytelnymi nagłówkami; „document" bez etykiety
    public void Distinct_sections_kept_in_order()
    {
        var body = DocumentBody.Assemble(
        [
            ("sentencja", "Sąd oddalił skargę."),
            ("uzasadnienie", "Skarżący wniósł..."),
        ]);

        Assert.Equal(["Sentencja", "Uzasadnienie"], body.Select(b => b.Label));
        Assert.Equal("Sąd oddalił skargę.", body[0].Text);
    }

    [Fact]
    public void Document_label_has_no_header()
    {
        var body = DocumentBody.Assemble([("document", "Krótkie postanowienie.")]);
        Assert.Null(Assert.Single(body).Label);
    }

    [Fact] // brak zakładki → zwykłe sklejenie
    public void No_overlap_plain_join()
    {
        var body = DocumentBody.Assemble(
        [
            ("uzasadnienie", "Pierwszy akapit."),
            ("uzasadnienie", "Drugi akapit."),
        ]);
        Assert.Equal("Pierwszy akapit.\nDrugi akapit.", Assert.Single(body).Text);
    }

    [Fact]
    public void Empty_yields_empty()
        => Assert.Empty(DocumentBody.Assemble([]));
}
