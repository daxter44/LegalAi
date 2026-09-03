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

    // --- AJ-5: zestaw werdyktów D3 + linie NARUSZA / DO ROZWAŻENIA ---

    [Theory]
    [InlineData("WERDYKT: OK\nZgodne [1].", UnitVerdict.Ok)]
    [InlineData("WERDYKT: RYZYKO WYSOKIE\nx", UnitVerdict.RiskHigh)]
    [InlineData("Werdykt: ryzyko niskie\nx", UnitVerdict.RiskLow)]
    [InlineData("WERDYKT: RYZYKO\nx", UnitVerdict.Risk)] // legacy bez wagi
    [InlineData("WERDYKT: BEZ TREŚCI PRAWNEJ\nx", UnitVerdict.NoLegalContent)]
    [InlineData("WERDYKT: POZA ZAKRESEM\nplan miejscowy", UnitVerdict.OutOfScope)]
    [InlineData("WERDYKT: BRAK PODSTAWY\nx", UnitVerdict.NoSources)]
    [InlineData("WERDYKT: BRAK ŹRÓDEŁ\nx", UnitVerdict.NoSources)] // stary prompt
    [InlineData("Nie wiem.", UnitVerdict.Unknown)]
    public void ParseUnit_maps_first_line_to_verdict(string full, UnitVerdict expected) =>
        Assert.Equal(expected, AnalysisPrompts.ParseUnit(full).Verdict);

    [Fact]
    public void ParseUnit_extracts_violates_and_suggestion_out_of_answer()
    {
        var full = """
            WERDYKT: RYZYKO WYSOKIE
            NARUSZA: art. 6 ust. 1 ustawy o ochronie praw lokatorów [2] — kaucja maks. 12-krotność czynszu
            DO ROZWAŻENIA: obniżyć kaucję do najwyżej dwunastokrotności miesięcznego czynszu.
            Kaucja w wysokości 25-krotności przekracza ustawowy limit [2], co czyni postanowienie nieważnym w tej części [1].
            """;
        var p = AnalysisPrompts.ParseUnit(full);
        Assert.Equal(UnitVerdict.RiskHigh, p.Verdict);
        Assert.StartsWith("art. 6 ust. 1", p.Violates);
        Assert.StartsWith("obniżyć kaucję", p.Suggestion);
        Assert.StartsWith("Kaucja w wysokości", p.Answer);
        Assert.DoesNotContain("NARUSZA", p.Answer);
        Assert.DoesNotContain("DO ROZWAŻENIA", p.Answer);
    }

    [Fact]
    public void ParseUnit_tolerates_markdown_bullets_and_missing_action_lines()
    {
        var p = AnalysisPrompts.ParseUnit("WERDYKT: RYZYKO NISKIE\n- **NARUSZA:** art. 483 KC [1]\nUzasadnienie.");
        Assert.Equal("art. 483 KC [1]", p.Violates);
        Assert.Null(p.Suggestion);
        Assert.Equal("Uzasadnienie.", p.Answer);

        var ok = AnalysisPrompts.ParseUnit("WERDYKT: OK\nZgodne [1].");
        Assert.Null(ok.Violates);
        Assert.Null(ok.Suggestion);
        Assert.Equal("Zgodne [1].", ok.Answer);
    }

    [Fact]
    public void Labels_follow_D3_wording_and_IsRisk_covers_all_risk_kinds()
    {
        Assert.Equal("RYZYKO WYSOKIE", AnalysisPrompts.Label(UnitVerdict.RiskHigh));
        Assert.Equal("RYZYKO NISKIE", AnalysisPrompts.Label(UnitVerdict.RiskLow));
        Assert.Equal("BEZ TREŚCI PRAWNEJ", AnalysisPrompts.Label(UnitVerdict.NoLegalContent));
        Assert.Equal("POZA ZAKRESEM", AnalysisPrompts.Label(UnitVerdict.OutOfScope));
        Assert.Equal("BRAK PODSTAWY", AnalysisPrompts.Label(UnitVerdict.NoSources));
        Assert.All(new[] { UnitVerdict.Risk, UnitVerdict.RiskHigh, UnitVerdict.RiskLow }, v => Assert.True(v.IsRisk()));
        Assert.All(new[] { UnitVerdict.Ok, UnitVerdict.NoSources, UnitVerdict.NoLegalContent, UnitVerdict.OutOfScope, UnitVerdict.Error, UnitVerdict.Unknown },
            v => Assert.False(v.IsRisk()));
    }

    [Fact]
    public void Map_prompt_lists_all_six_verdicts_and_action_lines()
    {
        var q = AnalysisPrompts.MapQuestion("oceń", Unit);
        foreach (var label in new[] { "WERDYKT: OK", "WERDYKT: RYZYKO WYSOKIE", "WERDYKT: RYZYKO NISKIE", "WERDYKT: BEZ TREŚCI PRAWNEJ", "WERDYKT: POZA ZAKRESEM", "WERDYKT: BRAK PODSTAWY", "NARUSZA:", "DO ROZWAŻENIA:" })
            Assert.Contains(label, q);
    }

    [Fact] // AJ-6: streszczenie dostaje nagłówek mechaniczny jako podstawę meta-wniosku (D2)
    public void Summary_input_carries_headline_before_digest()
    {
        var input = AnalysisPrompts.SummaryInput("czy warto się odwołać?",
        [
            new UnitDigest("§ 1", UnitVerdict.Ok, "ok"),
            new UnitDigest("§ 2", UnitVerdict.RiskHigh, "kaucja za wysoka [1]"),
        ]);
        Assert.Contains("Pytanie użytkownika: czy warto się odwołać?", input);
        Assert.Contains("Nagłówek: 1 z 2 fragmentów z ryzykiem (wysokie: § 2).", input);
        Assert.True(input.IndexOf("Nagłówek:") < input.IndexOf("§ 1: OK"));
        Assert.Contains("§ 2: RYZYKO WYSOKIE — kaucja za wysoka", input); // digest bez [n]
        Assert.Contains("ODPOWIADA WPROST", AnalysisPrompts.SummarySystemPrompt);
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
