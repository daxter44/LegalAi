using PrawoRAG.Api.Services;

namespace PrawoRAG.Tests.Analysis;

/// <summary>
/// T-SPK-1 — podział załącznika na jednostki analizy: struktura § na początku linii, tekst „płaski"
/// (PdfPig bez łamań) z filtrem odwołań, numerowane punkty pisma, fallback akapitowy, filtr śmieci
/// (krótkie nagłówki/podpisy) i cięcie za długich jednostek.
/// </summary>
public class LegalUnitSplitterTests
{
    private static string Filler(int chars) => string.Join(" ",
        Enumerable.Repeat("Strony zgodnie postanawiają o wzajemnych obowiązkach wynikających z umowy.", 1 + chars / 74))[..chars];

    [Fact]
    public void Contract_with_sections_splits_per_section_and_keeps_preamble()
    {
        var text = $"""
            UMOWA NAJMU LOKALU
            zawarta w Warszawie pomiędzy Janem Kowalskim (Wynajmujący) a Anną Nowak (Najemca),
            zwanymi dalej łącznie Stronami, o treści następującej.
            § 1
            Wynajmujący oddaje Najemcy lokal mieszkalny przy ul. Długiej 1 do używania na cele mieszkaniowe.
            § 2
            Najemca zobowiązuje się płacić czynsz w wysokości 3000 zł miesięcznie, zgodnie z § 1 powyżej,
            do dziesiątego dnia każdego miesiąca kalendarzowego.
            § 3
            Umowa zostaje zawarta na czas nieoznaczony z trzymiesięcznym okresem wypowiedzenia dla każdej ze stron.
            """;

        var units = LegalUnitSplitter.Split([text]);

        Assert.Equal(["wstęp", "§ 1", "§ 2", "§ 3"], units.Select(u => u.Heading));
        Assert.Equal([1, 2, 3, 4], units.Select(u => u.Index));
        Assert.StartsWith("§ 2", units[2].Text);           // tekst jednostki zawiera nagłówek
        Assert.Contains("czynsz", units[2].Text);
        Assert.DoesNotContain("czas nieoznaczony", units[2].Text); // treść § 3 nie przecieka do § 2
    }

    [Fact] // odwołanie „zgodnie z § 1" w środku linii nie tworzy podziału (tryb line-start)
    public void Inline_reference_does_not_split()
    {
        var text = $"""
            § 1
            {Filler(100)}
            § 2
            Czynsz płatny zgodnie z § 1 powyżej. {Filler(80)}
            § 3
            {Filler(100)}
            """;

        var units = LegalUnitSplitter.Split([text]);

        Assert.Equal(["§ 1", "§ 2", "§ 3"], units.Select(u => u.Heading));
    }

    [Fact] // tekst „płaski" bez łamań linii (PdfPig): nagłówki wykrywane w środku, odwołania odfiltrowane
    public void Flat_text_without_newlines_splits_and_filters_references()
    {
        var text =
            $"UMOWA. Postanowienia ogólne stron. " +
            $"§ 1 . Przedmiotem umowy jest najem lokalu. {Filler(80)} " +
            $"§ 2 . Czynsz wynosi 3000 zł, płatny zgodnie z § 1 do dziesiątego dnia miesiąca. {Filler(80)} " +
            $"§ 3 . Umowa na czas nieoznaczony, z zastrzeżeniem § 5 zdanie drugie. {Filler(80)} " +
            $"§ 4 . Zmiany umowy wymagają formy pisemnej pod rygorem nieważności. {Filler(80)}";

        var units = LegalUnitSplitter.Split([text]);

        Assert.Equal(4, units.Count(u => u.Heading.StartsWith('§')));
        Assert.Equal(["§ 1", "§ 2", "§ 3", "§ 4"], units.Where(u => u.Heading.StartsWith('§')).Select(u => u.Heading));
    }

    [Fact] // pismo z numerowanymi punktami na początku linii
    public void Numbered_letter_splits_per_point()
    {
        var text = $"""
            W odpowiedzi na wezwanie z dnia 1 marca wskazuję, co następuje, przedstawiając stanowisko strony.
            1. {Filler(90)}
            2. {Filler(90)}
            3. {Filler(90)}
            """;

        var units = LegalUnitSplitter.Split([text]);

        Assert.Contains("pkt 1", units.Select(u => u.Heading));
        Assert.Contains("pkt 3", units.Select(u => u.Heading));
    }

    [Fact] // brak struktury → fallback akapitowy (fragment 1..n)
    public void Unstructured_text_falls_back_to_fragments()
    {
        var pages = new[] { Filler(2000), Filler(1500) };

        var units = LegalUnitSplitter.Split(pages);

        Assert.True(units.Count >= 2);
        Assert.All(units, u => Assert.StartsWith("fragment", u.Heading));
        Assert.Equal(Enumerable.Range(1, units.Count), units.Select(u => u.Index));
    }

    [Fact] // krótkie jednostki (podpisy, stopki) odpadają
    public void Short_junk_units_are_dropped()
    {
        var text = $"""
            § 1
            {Filler(100)}
            § 2
            {Filler(100)}
            § 3
            {Filler(100)}
            § 4
            Podpisy stron.
            """;

        var units = LegalUnitSplitter.Split([text]);

        Assert.DoesNotContain("§ 4", units.Select(u => u.Heading));
        Assert.Equal(3, units.Count);
    }

    [Fact] // jednostka dłuższa niż limit → części „(cz. n)", każda ≤ limit
    public void Oversize_unit_is_split_into_parts()
    {
        var text = $"""
            § 1
            {Filler(100)}
            § 2
            {Filler(8000)}
            § 3
            {Filler(100)}
            """;

        var units = LegalUnitSplitter.Split([text]);

        var parts = units.Where(u => u.Heading.StartsWith("§ 2")).ToList();
        Assert.True(parts.Count >= 2);
        Assert.Equal("§ 2 (cz. 1)", parts[0].Heading);
        Assert.All(parts, p => Assert.True(p.Text.Length <= LegalUnitSplitter.MaxUnitChars));
        Assert.Equal(Enumerable.Range(1, units.Count), units.Select(u => u.Index)); // indeksy ciągłe po cięciu
    }

    [Fact] // ustawa/rozporządzenie: struktura po „Art."
    public void Statute_like_text_splits_per_article()
    {
        var text = $"""
            Art. 1. {Filler(90)}
            Art. 2. {Filler(90)}
            Art. 3. {Filler(90)}
            """;

        var units = LegalUnitSplitter.Split([text]);

        Assert.Equal(3, units.Count);
        Assert.StartsWith("Art. 2", units[1].Heading);
    }

    [Fact]
    public void Empty_pages_yield_no_units()
    {
        Assert.Empty(LegalUnitSplitter.Split([]));
        Assert.Empty(LegalUnitSplitter.Split(["", "   "]));
    }
}
