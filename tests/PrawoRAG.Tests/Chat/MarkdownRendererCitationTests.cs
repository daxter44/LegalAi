using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Chat;

/// <summary>
/// T-CITE-RENDER (Zadanie 4 planu SAS) — klikalne cytowania, w tym GRUPY <c>[2, 3, 4]</c>.
///
/// Zgłoszone jako problem UI: model czasem pisze grupę zamiast osobnych markerów, a wtedy nic nie
/// było linkowane. Naprawa po stronie ODCZYTU (nie promptu), bo działa też na JUŻ ZAPISANYCH
/// odpowiedziach, które UI renderuje ponownie po wczytaniu rozmowy z historii.
/// </summary>
public class MarkdownRendererCitationTests
{
    private const string Anchor = "abc123";

    private static string Render(string markdown, int sources = 8, int docs = 0) =>
        MarkdownRenderer.ToSafeHtml(markdown, sources, Anchor, docs);

    private static int LinkCount(string html) =>
        html.Split("<a class=\"cite\"").Length - 1;

    [Fact] // Grupa => TRZY osobne linki, kazdy z wlasna kotwica.
    public void Group_becomes_separate_links()
    {
        var html = Render("Wynika to z przepisów [2, 3, 4].");

        Assert.Equal(3, LinkCount(html));
        Assert.Contains($"href=\"#src-{Anchor}-2\"", html);
        Assert.Contains($"href=\"#src-{Anchor}-3\"", html);
        Assert.Contains($"href=\"#src-{Anchor}-4\"", html);
    }

    [Fact] // Przecinek NIE zostaje poza linkiem - inaczej w tekscie wisialby osierocony znak.
    public void Separator_is_space_not_comma()
    {
        var html = Render("Przepisy [2, 3] mówią.");

        Assert.Contains("</a> <a class=\"cite\"", html);
        Assert.DoesNotContain("</a>,", html);
    }

    [Theory] // Warianty zapisu, ktore realnie produkuja modele.
    [InlineData("[2,3]")]
    [InlineData("[2, 3]")]
    [InlineData("[ 2 , 3 ]")]
    public void Group_separator_variants(string marker)
    {
        Assert.Equal(2, LinkCount(Render($"Teza {marker} jest oparta.")));
    }

    [Fact] // Pojedynczy marker dziala jak dotad (rownowaznosc).
    public void Single_marker_still_links()
    {
        var html = Render("Teza [2].");

        Assert.Equal(1, LinkCount(html));
        Assert.Contains($"href=\"#src-{Anchor}-2\"", html);
    }

    [Fact] // Numer SPOZA zakresu w grupie NIE jest linkowany, ale reszta grupy tak - parytet
           // z dotychczasowym zachowaniem dla pojedynczych markerow (kandydat na fabrykacje).
    public void Out_of_range_in_group_is_not_linked()
    {
        var html = Render("Zgodnie z [2, 99].", sources: 8);

        Assert.Equal(1, LinkCount(html));                       // tylko [2]
        Assert.Contains($"href=\"#src-{Anchor}-2\"", html);
        Assert.DoesNotContain($"#src-{Anchor}-99", html);
        Assert.Contains("[99]", html);                           // zostaje tekstem
    }

    [Fact] // Zaden numer w zakresie => marker zostaje NIETKNIETY (mniej niespodzianek w tekscie).
    public void Group_entirely_out_of_range_is_left_alone()
    {
        var html = Render("Zgodnie z [98, 99].", sources: 8);

        Assert.Equal(0, LinkCount(html));
        Assert.Contains("[98, 99]", html);
    }

    [Fact] // Przestrzen zalacznika: [D1, D2] tez rozbijana.
    public void Doc_group_becomes_separate_links()
    {
        var html = Render("Umowa mówi [D1, D2].", sources: 0, docs: 2);

        Assert.Equal(2, LinkCount(html));
        Assert.Contains($"href=\"#docsrc-{Anchor}-1\"", html);
        Assert.Contains($"href=\"#docsrc-{Anchor}-2\"", html);
    }

    [Theory] // Tekst NIEBEDACY cytowaniem nie moze byc linkowany.
    [InlineData("Termin [2 marca] upłynął.")]
    [InlineData("Zobacz [ustawa] wyżej.")]
    public void Non_citation_brackets_are_not_linked(string text)
    {
        Assert.Equal(0, LinkCount(Render(text)));
    }

    [Fact] // Duze numery (przy rozszerzeniu sasiedztwa zrodel bywa kilkadziesiat) - stary regex
           // mial limit 2 cyfr, wiec [12] dzialalo, ale warto miec to pod testem po zmianie wzorca.
    public void Two_digit_numbers_link_correctly()
    {
        var html = Render("Zgodnie z [12, 34].", sources: 40);

        Assert.Equal(2, LinkCount(html));
        Assert.Contains($"href=\"#src-{Anchor}-34\"", html);
    }
}
