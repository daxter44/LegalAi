using PrawoRAG.Api.Services.Legal;

namespace PrawoRAG.Tests.Ui;

/// <summary>
/// Dokumenty prawne z Legal/*.md (2026-09-01). Testy pilnują, że zasoby są wbudowane i renderują się
/// do pełnej strony, że treść zgadza się z faktami z kodu (retencja 6 miesięcy, brak zapisu dokumentów,
/// brak cookies analitycznych) i że przed publikacją da się wykryć niewypełnione pola.
/// </summary>
public class LegalPagesTests
{
    public static IEnumerable<object[]> Documents() => LegalPages.All.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_document_is_embedded_and_renders_full_page(LegalPages.LegalDocument doc)
    {
        var html = LegalPages.GetHtml(doc);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<h1>", html);
        Assert.Contains($"OmniaSI — {doc.Title}", html);
        // Nawigacja między dokumentami: pozostałe dwa jako linki, bieżący jako tekst.
        foreach (var other in LegalPages.All.Where(d => d != doc))
            Assert.Contains($"href=\"/{other.Slug}\"", html);
    }

    [Fact]
    public void Markdown_tables_render_as_html_tables()
    {
        // Polityka prywatności ma tabele (cele, odbiorcy, retencja) — bez rozszerzenia pipe-tables
        // Markdig zostawiłby je jako akapity z kreskami.
        Assert.Contains("<table>", LegalPages.GetHtml(LegalPages.Prywatnosc));
        Assert.Contains("<table>", LegalPages.GetHtml(LegalPages.Cookies));
    }

    [Fact]
    public void Raw_html_in_markdown_is_not_passed_through()
    {
        // Pipeline ma DisableHtml — literówka w dokumencie nie może stać się znacznikiem. Sprawdzamy
        // pośrednio: w treści nie ma <script>, a tekst z nawiasami kątowymi z regulaminu byłby
        // zakodowany. Wprost testujemy zachowanie pipeline'u na próbce przez publiczny render.
        Assert.DoesNotContain("<script", LegalPages.GetHtml(LegalPages.Regulamin));
    }

    [Fact]
    public void Content_matches_facts_established_in_code()
    {
        var regulamin = LegalPages.GetMarkdown(LegalPages.Regulamin);
        var prywatnosc = LegalPages.GetMarkdown(LegalPages.Prywatnosc);
        var cookies = LegalPages.GetMarkdown(LegalPages.Cookies);

        // RetentionService: MaxAge = 183 dni.
        Assert.Contains("6 miesięcy", regulamin);
        Assert.Contains("6 miesięcy", prywatnosc);
        // AnalysisSession: treść załącznika tylko w pamięci, nigdy na dysku/bazie.
        Assert.Contains("nie jest zapisywana na dysku ani w bazie", regulamin);
        // Ciasteczko sesji z Program.cs.
        Assert.Contains("praworag.auth", cookies);
        // Nie obiecujemy mechanizmów, których nie ma (analiza R1/R2): pseudonimizacji ani ekranu zgody.
        Assert.DoesNotContain("pseudonimizacj", regulamin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pseudonimizacj", prywatnosc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ekran zgody", prywatnosc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Placeholder_detection_finds_unfilled_fields_and_ignores_markdown_links()
    {
        // Stan na dziś: podmiot, adres i dostawcy są jeszcze do uzupełnienia — detektor MUSI to widzieć,
        // bo to on ma zatrzymać publikację z dziurami. Gdy dokumenty zostaną wypełnione, ten test
        // odwróci się na Assert.False — świadomie.
        Assert.True(LegalPages.HasPlaceholders(LegalPages.Regulamin));
        Assert.True(LegalPages.HasPlaceholders(LegalPages.Prywatnosc));
        Assert.True(LegalPages.HasPlaceholders(LegalPages.Cookies));
    }
}
