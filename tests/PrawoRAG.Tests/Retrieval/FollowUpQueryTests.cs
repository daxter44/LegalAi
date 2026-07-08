using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// Heurystyka follow-upów (czysta — bez DB/LLM): sklejenie poprzednich pytań użytkownika z dopytaniem
/// buduje wariant zapytania z kontekstem. Kolejność chronologiczna, cap na 2 ostatnie poprzednie pytania.
/// </summary>
public class FollowUpQueryTests
{
    [Fact]
    public void Empty_history_returns_question_unchanged()
        => Assert.Equal("a co z § 2?", FollowUpQuery.Contextualize([], "a co z § 2?"));

    [Fact]
    public void Joins_previous_questions_chronologically_before_current()
        => Assert.Equal(
            "co mówi art. 367 KPC? a co z § 2?",
            FollowUpQuery.Contextualize(["co mówi art. 367 KPC?"], "a co z § 2?"));

    [Fact]
    public void Caps_at_two_most_recent_previous_questions()
        => Assert.Equal(
            "pytanie B pytanie C dopytanie",
            FollowUpQuery.Contextualize(["pytanie A", "pytanie B", "pytanie C"], "dopytanie"));

    [Fact]
    public void Skips_blank_previous_questions()
        => Assert.Equal(
            "pytanie A dopytanie",
            FollowUpQuery.Contextualize(["pytanie A", "  ", ""], "dopytanie"));

    // --- PickContextual: wybór ASYMETRYCZNY z marginesem (fałszywe surowe >> fałszywe kontekstowe) ---

    [Fact]
    public void Noise_level_difference_picks_contextual()
    {
        // Regresja z M4: surowe „a co z § 2?" 0.879001 do PRZYPADKOWYCH fragmentów vs kontekstowe
        // 0.879000 do właściwego artykułu — różnica 1e-6 to szum, nie sygnał. Ostre `>` wybierało
        // gorszy surowy wariant.
        Assert.True(FollowUpQuery.PickContextual(rawSignal: 0.879008, contextualSignal: 0.879000));
    }

    [Fact]
    public void Raw_clearly_stronger_beats_margin_and_wins()
        => Assert.False(FollowUpQuery.PickContextual(rawSignal: 0.85, contextualSignal: 0.60));

    [Fact]
    public void Raw_within_margin_still_loses()
        // Surowe wyżej, ale o mniej niż margines → kontekstowe (asymetria kosztów pomyłek).
        => Assert.True(FollowUpQuery.PickContextual(
            rawSignal: 0.879, contextualSignal: 0.879 - FollowUpQuery.DefaultSignalMargin + 0.001));

    [Fact]
    public void Custom_margin_is_respected()
    {
        Assert.False(FollowUpQuery.PickContextual(0.70, 0.65, margin: 0.02)); // 0.05 > 0.02 → surowe
        Assert.True(FollowUpQuery.PickContextual(0.70, 0.65, margin: 0.10));  // 0.05 ≤ 0.10 → kontekstowe
    }
}
