using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Analysis;

/// <summary>AJ-3 — profil dokumentu: parser liniowy (pełny / częściowy / pusty / śmieci), strażnik
/// czystości (profil z oceną prawną → odrzucony), próbka do budżetu, kotwica retrievalu i blok
/// do promptu.</summary>
public class DocumentProfileTests
{
    [Fact]
    public void Parses_full_profile()
    {
        var text = """
            TYP: umowa najmu lokalu mieszkalnego na czas oznaczony
            STRONY: Jan Kowalski – wynajmujący (osoba fizyczna); Anna Nowak – najemca (konsument)
            PRZEDMIOT: lokal mieszkalny nr 4 przy ul. Kwiatowej 15 w Poznaniu
            DEFINICJE: Lokal, Wyposażenie
            AKTY: Kodeks cywilny
            ORZECZENIA: brak
            """;
        var p = DocumentProfilePrompts.Parse(text);
        Assert.NotNull(p);
        Assert.Equal("umowa najmu lokalu mieszkalnego na czas oznaczony", p!.Kind);
        Assert.Contains("konsument", p.Parties);
        Assert.Equal("Lokal, Wyposażenie", p.Definitions);
        Assert.Equal("Kodeks cywilny", p.CitedActs);
        Assert.Null(p.CitedJudgments); // „brak" = placeholder, pole puste
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void Tolerates_markdown_bold_and_case_and_ignores_unknown_lines()
    {
        var text = """
            Oto profil dokumentu:
            **Typ:** regulamin sklepu internetowego
            **Strony**: Sprzedawca (przedsiębiorca), Klient (konsument)
            Komentarz: to jest zwyczajny regulamin.
            """;
        var p = DocumentProfilePrompts.Parse(text);
        Assert.NotNull(p);
        Assert.Equal("regulamin sklepu internetowego", p!.Kind);
        Assert.Null(p.Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nie potrafię określić.")]
    [InlineData("TYP:\nSTRONY: -")]
    public void No_recognised_lines_means_null(string? text) =>
        Assert.Null(DocumentProfilePrompts.Parse(text));

    [Theory]
    [InlineData("TYP: umowa najmu\nSTRONY: najemca; wynajmujący narusza obowiązki")]
    [InlineData("TYP: umowa niezgodna z ustawą")]
    [InlineData("TYP: umowa\nDEFINICJE: klauzula abuzywna")]
    [InlineData("TYP: umowa\nAKTY: art. 6 uopl [1]")]
    public void Guard_rejects_profiles_with_legal_assessment(string text)
    {
        var p = DocumentProfilePrompts.Parse(text);
        Assert.NotNull(p);
        Assert.False(DocumentProfilePrompts.IsClean(p!));
        Assert.Null(DocumentProfilePrompts.ParseClean(text));
    }

    [Fact]
    public void Guard_accepts_plain_facts()
    {
        var text = "TYP: umowa o dzieło między przedsiębiorcami\nSTRONY: Meblex sp. z o.o. (zamawiający), Jan Stolarz (wykonawca, przedsiębiorca)";
        Assert.NotNull(DocumentProfilePrompts.ParseClean(text));
    }

    [Fact]
    public void Prompt_block_skips_empty_fields()
    {
        var p = new DocumentProfile("umowa", null, "przedmiot", null, null, null);
        var block = p.ToPromptBlock();
        Assert.Equal("Rodzaj dokumentu: umowa\nPrzedmiot: przedmiot", block);
    }

    [Fact]
    public void Sample_takes_leading_units_within_budget_and_always_the_first()
    {
        var units = new List<DocUnit>
        {
            new(1, "wstęp", new string('a', 1000)),
            new(2, "§ 1", new string('b', 1500)),
            new(3, "§ 2", new string('c', 1000)), // 1000+1500+1000 > 3000 → odpada
            new(4, "§ 3", new string('d', 100)),  // kolejność dokumentu — nie „dopychamy" mniejszymi
        };
        var sample = DocumentProfilePrompts.BuildSample(units, budget: 3000);
        Assert.StartsWith(new string('a', 1000), sample);
        Assert.Contains(new string('b', 1500), sample);
        Assert.DoesNotContain("ccc", sample);
        Assert.DoesNotContain("ddd", sample);

        var huge = DocumentProfilePrompts.BuildSample([new(1, "wstęp", new string('x', 10_000))], budget: 3000);
        Assert.Equal(3000, huge.Length);
    }
}
