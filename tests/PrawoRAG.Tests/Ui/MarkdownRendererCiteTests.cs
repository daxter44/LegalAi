using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Ui;

/// <summary>
/// T-CITE — klikalne cytowania [n] w odpowiedzi (UI): markery w zakresie 1..sourceCount stają się
/// kotwicami do kart źródeł, spoza zakresu zostają tekstem (kandydaci na fabrykację — nie linkujemy
/// w próżnię). Podmiana działa na ZSANITYZOWANYM HTML — bezpieczeństwo XSS bazowego wariantu
/// nie może zregresować.
/// </summary>
public class MarkdownRendererCiteTests
{
    [Fact] // marker w zakresie → kotwica z prefiksem wymiany
    public void In_range_marker_becomes_anchor()
    {
        var html = MarkdownRenderer.ToSafeHtml("Odpowiedzialność wymaga winy [1].", sourceCount: 3, anchorId: "abc123");
        Assert.Contains("<a class=\"cite\" href=\"#src-abc123-1\"", html);
        Assert.Contains(">[1]</a>", html);
    }

    [Fact] // wiele markerów, każdy do własnego źródła
    public void Multiple_markers_link_to_their_sources()
    {
        var html = MarkdownRenderer.ToSafeHtml("Teza [1], kontrprzykład [3].", 3, "x");
        Assert.Contains("#src-x-1", html);
        Assert.Contains("#src-x-3", html);
    }

    [Fact] // [n] spoza zakresu (fabrykacja/za mało źródeł) → zwykły tekst, zero martwych linków
    public void Out_of_range_marker_stays_plain_text()
    {
        var html = MarkdownRenderer.ToSafeHtml("Teza [5].", sourceCount: 2, anchorId: "x");
        Assert.DoesNotContain("<a class=\"cite\"", html);
        Assert.Contains("[5]", html);
    }

    [Fact] // zero źródeł (np. streaming zanim przyszły) → zachowanie identyczne z bazowym wariantem
    public void Zero_sources_behaves_like_plain_renderer()
    {
        Assert.Equal(MarkdownRenderer.ToSafeHtml("Teza [1]."), MarkdownRenderer.ToSafeHtml("Teza [1].", 0, "x"));
    }

    [Fact] // sanityzacja nadal działa — wstrzyknięty skrypt nie przechodzi mimo nowej ścieżki
    public void Sanitization_still_strips_injected_html()
    {
        var html = MarkdownRenderer.ToSafeHtml("<script>alert(1)</script> Teza [1].", 1, "x");
        Assert.DoesNotContain("<script", html);
        Assert.Contains("#src-x-1", html); // a cytowanie dalej linkuje
    }

    [Fact] // [10] (dwie cyfry) w zakresie działa; [123] (trzy cyfry) to nie marker cytowania
    public void Two_digit_markers_supported_three_digit_ignored()
    {
        var html = MarkdownRenderer.ToSafeHtml("Źródło [10] i liczba [123].", 12, "x");
        Assert.Contains("#src-x-10", html);
        Assert.DoesNotContain("#src-x-123", html);
    }
}
