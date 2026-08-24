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

    [Fact] // trafienie DOKŁADNE (sygnatura/Dz.U./cytat z pytania) przepuszcza bramkę mimo niskiego cosine
    public void Does_not_abstain_on_exact_match_below_threshold()
    {
        // Realny przypadek: goła sygnatura „III SA/Po 154/26" embeduje się bezwartościowo (identyfikator,
        // nie zapytanie semantyczne), więc cosine leci poniżej progu DOKŁADNIE wtedy, gdy w kontekście
        // leży orzeczenie wprost wskazane przez użytkownika.
        var r = Result(0.30) with { ExactMatchHits = 1 };
        Assert.False(AbstentionPolicy.ShouldAbstain(r, 0.55));
    }

    [Fact] // brak kandydatów wygrywa z sygnałem exact-match (nie ma czego pokazać)
    public void Abstains_on_empty_even_with_exact_match() =>
        Assert.True(AbstentionPolicy.ShouldAbstain(new RetrievalResult([], 0, null, 1), 0.55));

    [Fact] // most cytowań (sygnał POCHODNY) NIE przepuszcza bramki — świadome ograniczenie wyjątku
    public void Bridge_alone_does_not_open_the_gate()
    {
        // Most nie zwiększa ExactMatchHits (patrz HybridRetriever: liczy tylko sygnaturę/akt/cytat),
        // więc pytanie opisowe bez pokrycia dalej kończy się odmową — próg cosine zostaje jedyną obroną.
        var r = Result(0.30);
        Assert.True(AbstentionPolicy.ShouldAbstain(r, 0.55));
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

    // --- Zadanie 9 planu ROU: rozdzielenie sygnałów + normalizacja wariantów zapisu ---
    // Powód: `IsClean` przestaje być kosmetyką (badge ⚠) i staje się BRAMKĄ (Zadanie 10), która
    // zawraca odpowiedź do regeneracji albo ją blokuje. Sygnał musi więc (a) być rozdzielony,
    // bo artykuły i sygnatury mają RÓŻNĄ precyzję, (b) nie produkować fałszywych alarmów na
    // wariantach zapisu, które w aktach są normą.

    [Fact] // Rozdzial sygnalow: sygnatura jest wysokoprecyzyjna (waski regex), artykul zaszumiony.
           // Bramka moze je traktowac inaczej tylko wtedy, gdy przychodza osobno.
    public void Splits_suspicious_articles_from_case_numbers()
    {
        var ctx = new[] { "Wyrok dotyczył wykroczenia drogowego." };
        var check = CitationValidator.Validate("Zgodnie z art. 999 oraz wyrokiem I ACa 123/45 [1].", ctx, 1);

        Assert.Contains(check.SuspiciousArticles, s => s.Contains("999"));
        Assert.Contains(check.SuspiciousCaseNumbers, s => s.Contains("I ACa 123/45"));
        // Suma zostaje jako kompatybilność wstecz (czyta ją UI i eval).
        Assert.Equal(
            check.SuspiciousArticles.Count + check.SuspiciousCaseNumbers.Count,
            check.SuspiciousReferences.Count);
    }

    [Theory] // NORMALIZACJA. Lewa strona = jak model pisze w odpowiedzi, prawa = jak stoi w akcie.
             // Bez tego bramka zawracalaby POPRAWNE odpowiedzi - a to zamiana halucynacji na odmowy,
             // czyli porazka, nie sukces (prog zabicia: >10% falszywych alarmow).
    [InlineData("art. 5 ust. 1", "Art. 5. 1. Przepis stanowi, że…")]        // ust. vs numeracja w akcie
    [InlineData("art. 5 § 2", "Art. 5. § 2. Przepis stanowi, że…")]          // paragraf rozdzielony kropką
    [InlineData("art. 5 pkt 3", "Art. 5. Przepis wymienia: 3) trzeci punkt")] // pkt jako wyliczenie
    [InlineData("art. 5 ust. 1 pkt 2", "Art. 5. 1. 2) treść punktu")]         // łańcuch jednostek
    public void Article_unit_suffixes_do_not_create_false_alarms(string inAnswer, string inContext)
    {
        var check = CitationValidator.Validate($"Zgodnie z {inAnswer} [1] tak właśnie jest.", [inContext], 1);

        Assert.Empty(check.SuspiciousArticles);
        Assert.True(check.IsClean);
    }

    [Fact] // Normalizacja NIE MOZE przepuscic zmyslonego numeru artykulu - inaczej bramka staje sie
           // bezuzyteczna. Rdzen numeru musi byc obecny w kontekscie, samo obcięcie sufiksu nie wystarcza.
    public void Normalization_still_catches_wrong_article_number()
    {
        var ctx = new[] { "Art. 5. 1. Przepis stanowi, że…" };
        var check = CitationValidator.Validate("Zgodnie z art. 7 ust. 1 [1].", ctx, 1);

        Assert.Contains(check.SuspiciousArticles, s => s.Contains('7'));
        Assert.False(check.IsClean);
    }

    [Fact] // Numer artykulu z litera (art. 1a u.p.o.l., art. 43bb) - realny przypadek z korpusu,
           // rdzen nie moze zgubic litery, bo art. 1a to INNY przepis niz art. 1.
    public void Article_number_with_letter_is_not_confused_with_bare_number()
    {
        var ctx = new[] { "Art. 1. Ustawa reguluje opodatkowanie nieruchomości." };
        var check = CitationValidator.Validate("Zgodnie z art. 1a ust. 1 pkt 2 [1].", ctx, 1);

        Assert.Contains(check.SuspiciousArticles, s => s.Contains("1a"));
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
