using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Grounding;

/// <summary>T-ABST — bramka abstynencji (czysta logika progu).</summary>
public class AbstentionPolicyTests
{
    private static RetrievalResult Result(double maxSim, int chunks = 1)
    {
        var list = Enumerable.Range(0, chunks).Select(_ => new RetrievedChunk
        {
            Text = "x", Source = "SAOS", DocType = "judgment", Title = "t", Score = 0.1, Similarity = maxSim,
        }).ToList();
        return new RetrievalResult(list, chunks == 0 ? 0 : maxSim);
    }

    [Fact] // pytanie spoza korpusu (niskie podobieństwo) → abstynencja
    public void Abstains_below_threshold() =>
        Assert.True(AbstentionPolicy.ShouldAbstain(Result(0.30), 0.55));

    [Fact] // brak kandydatów → abstynencja
    public void Abstains_on_empty() =>
        Assert.True(AbstentionPolicy.ShouldAbstain(new RetrievalResult([], 0), 0.55));

    [Fact] // pytanie w korpusie (wysokie podobieństwo) → przechodzi
    public void Passes_above_threshold() =>
        Assert.False(AbstentionPolicy.ShouldAbstain(Result(0.78), 0.55));

    [Fact] // próg faktycznie steruje
    public void Threshold_controls_decision()
    {
        var r = Result(0.60);
        Assert.False(AbstentionPolicy.ShouldAbstain(r, 0.55));
        Assert.True(AbstentionPolicy.ShouldAbstain(r, 0.70));
    }
}

/// <summary>T-FABR — anty-fabrykacja cytatów.</summary>
public class CitationValidatorTests
{
    [Fact] // #1: cytaty [1],[2] w zakresie, brak zmyślonych odniesień → czysto
    public void Clean_when_citations_in_range_and_grounded()
    {
        var ctx = new[] { "Sąd skazał oskarżonego.", "Wymierzono karę grzywny." };
        var check = CitationValidator.Validate("Sprawca został skazany [1], wymierzono grzywnę [2].", ctx, 2);

        Assert.True(check.IsClean);
        Assert.Equal([1, 2], check.Cited);
        Assert.Empty(check.OutOfRange);
    }

    [Fact] // #2: cytat [5] spoza zakresu (2 źródła) → wykryty
    public void Detects_out_of_range_citation()
    {
        var check = CitationValidator.Validate("Teza [5].", ["a", "b"], 2);
        Assert.Contains(5, check.OutOfRange);
        Assert.False(check.IsClean);
    }

    [Fact] // #3: zmyślony artykuł i sygnatura nieobecne w kontekście → podejrzane
    public void Flags_fabricated_article_and_case_number()
    {
        var ctx = new[] { "Wyrok dotyczył wykroczenia drogowego." };
        var check = CitationValidator.Validate("Zgodnie z art. 999 oraz wyrokiem I ACa 123/45 [1].", ctx, 1);

        Assert.Contains(check.SuspiciousReferences, s => s.Contains("999"));
        Assert.Contains(check.SuspiciousReferences, s => s.Contains("I ACa 123/45"));
        Assert.False(check.IsClean);
    }

    [Fact] // artykuł OBECNY w kontekście → nie jest podejrzany
    public void Article_present_in_context_is_not_suspicious()
    {
        var ctx = new[] { "Sąd zastosował art. 178a § 4 Kodeksu karnego." };
        var check = CitationValidator.Validate("Sprawca odpowiada z art. 178a § 4 [1].", ctx, 1);

        Assert.Empty(check.SuspiciousReferences);
        Assert.True(check.IsClean);
    }
}

/// <summary>AKT-4: AmendmentEffectiveDate z RetrievedChunk trafia do SourceRef (chip w UI).</summary>
public class GroundedPromptAmendmentTests
{
    private static RetrievedChunk Chunk(string text, string? amendmentDate = null) => new()
    {
        Text = text, Source = "ELI", DocType = "act", Title = "t", Score = 1,
        AmendmentEffectiveDate = amendmentDate,
    };

    [Fact] // zwykłe źródło → brak daty nowelizacji w SourceRef
    public void Regular_source_has_no_amendment_date()
    {
        var (_, sources) = GroundedPrompt.Build("pytanie", [Chunk("treść przepisu")]);
        Assert.Null(sources[0].AmendmentEffectiveDate);
    }

    [Fact] // źródło dołożone przez TemporalAugmenter → data przechodzi do SourceRef
    public void Amendment_source_carries_effective_date()
    {
        var (_, sources) = GroundedPrompt.Build("pytanie",
            [Chunk("[NOWELIZACJA — obowiązuje od 2026-07-08...]\ntreść zmiany", "2026-07-08")]);
        Assert.Equal("2026-07-08", sources[0].AmendmentEffectiveDate);
    }
}
