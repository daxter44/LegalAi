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
}
