using PrawoRAG.Llm.Analysis;

namespace PrawoRAG.Tests.Analysis;

/// <summary>AJ-4 — prompt fazy map z profilem dokumentu: bez profilu bajt w bajt jak dotąd (zero
/// regresji względem strojenia pod model), z profilem kotwica dziedzinowa w PIERWSZEJ linii (treść
/// pytania = zapytanie retrievalu) i blok KONTEKST DOKUMENTU nad fragmentem.</summary>
public class AnalysisPromptsTests
{
    private static readonly DocUnit Unit = new(5, "§ 5", "§ 5 Kaucja. Najemca wpłaca kaucję w wysokości 80 000 zł.");

    [Fact]
    public void Without_profile_prompt_is_unchanged()
    {
        var legacy = AnalysisPrompts.MapQuestion("oceń ryzyka", Unit);
        Assert.Equal(legacy, AnalysisPrompts.MapQuestion("oceń ryzyka", Unit, profile: null));
        Assert.Equal(legacy, AnalysisPrompts.MapQuestion("oceń ryzyka", Unit, new DocumentProfile(null, null, null, null, null, null)));
        Assert.StartsWith("oceń ryzyka", legacy);
        Assert.DoesNotContain("KONTEKST DOKUMENTU", legacy);
        Assert.Contains("Analizowany fragment dokumentu (§ 5)", legacy);
        Assert.Contains("WERDYKT: OK", legacy);
    }

    [Fact]
    public void With_profile_anchor_is_first_line_and_context_block_precedes_fragment()
    {
        var profile = new DocumentProfile(
            "umowa najmu lokalu mieszkalnego", "najemca – konsument", "lokal nr 4", "Lokal", "Kodeks cywilny", null);
        var q = AnalysisPrompts.MapQuestion("oceń ryzyka", Unit, profile);

        var firstLine = q.Split('\n')[0];
        Assert.Equal("Dokument: umowa najmu lokalu mieszkalnego; najemca – konsument.", firstLine);
        Assert.Contains("KONTEKST DOKUMENTU", q);
        Assert.Contains("Rodzaj dokumentu: umowa najmu lokalu mieszkalnego", q);
        Assert.Contains("Powołane akty: Kodeks cywilny", q);
        // Kontekst przed fragmentem, fragment przed instrukcją werdyktu — kolejność ma znaczenie dla modelu.
        Assert.True(q.IndexOf("KONTEKST DOKUMENTU") < q.IndexOf("Analizowany fragment dokumentu"));
        Assert.True(q.IndexOf("Analizowany fragment dokumentu") < q.IndexOf("WERDYKT: OK"));
        // Intencja użytkownika i treść jednostki nadal w prompcie.
        Assert.Contains("oceń ryzyka", q);
        Assert.Contains("80 000 zł", q);
    }

    [Fact] // AJ-4b: zapytanie retrievalu = kotwica + treść jednostki, bez intencji i bez instrukcji werdyktu
    public void Retrieval_query_is_anchor_plus_unit_text_without_instructions()
    {
        var profile = new DocumentProfile("umowa najmu lokalu mieszkalnego", "najemca – konsument", null, null, null, null);
        var q = AnalysisPrompts.RetrievalQuery(Unit, profile);
        Assert.Equal("umowa najmu lokalu mieszkalnego; najemca – konsument\n" + Unit.Text, q);
        Assert.DoesNotContain("WERDYKT", q);
        Assert.DoesNotContain("oceń", q);

        Assert.Equal(Unit.Text, AnalysisPrompts.RetrievalQuery(Unit, null));
        Assert.Equal(Unit.Text, AnalysisPrompts.RetrievalQuery(Unit, new DocumentProfile(null, null, "x", null, null, null)));
    }

    [Fact] // AJ-4b: długa jednostka ucinana do budżetu embeddera na granicy słowa
    public void Retrieval_query_truncates_long_unit_at_word_boundary()
    {
        var words = string.Join(" ", Enumerable.Repeat("postanowienie", 400)); // ~5600 zn
        var q = AnalysisPrompts.RetrievalQuery(new DocUnit(1, "§ 1", words), null);
        Assert.True(q.Length <= AnalysisPrompts.RetrievalQueryChars);
        Assert.EndsWith("postanowienie", q);
    }

    [Fact]
    public void Profile_without_anchor_fields_still_adds_context_but_no_anchor_line()
    {
        var profile = new DocumentProfile(null, null, "lokal nr 4", null, null, null);
        var q = AnalysisPrompts.MapQuestion("oceń ryzyka", Unit, profile);
        Assert.StartsWith("oceń ryzyka", q);
        Assert.Contains("KONTEKST DOKUMENTU", q);
        Assert.Contains("Przedmiot: lokal nr 4", q);
    }
}
