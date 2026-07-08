using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Grounding;

/// <summary>
/// Prompt z historią rozmowy (follow-upy): wcześniejsze tury wchodzą jako naprzemienne User/Assistant
/// PRZED finalną wiadomością z pytaniem+źródłami. Krytyczne niezmienniki: markery [n] zdjęte z odpowiedzi
/// historycznych (stara numeracja nie może przeciec do walidacji anty-fabrykacji bieżącej tury),
/// przycięcie długich odpowiedzi, scalanie kolejnych User (abstynencja) — ścisła naprzemienność ról
/// dla każdego providera. Build bez historii ≡ dotychczasowe zachowanie.
/// </summary>
public class GroundedPromptHistoryTests
{
    private static RetrievedChunk Chunk(string text = "Art. 367. Treść przepisu.") => new()
    {
        ChunkId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), Text = text,
        Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks postępowania cywilnego",
        Score = 1.0,
    };

    [Fact]
    public void Without_history_behaves_as_before()
    {
        var (req, sources) = GroundedPrompt.Build("pytanie", [Chunk()]);

        Assert.Equal(2, req.Messages.Count);
        Assert.Equal(ChatRole.System, req.Messages[0].Role);
        Assert.Equal(ChatRole.User, req.Messages[1].Role);
        Assert.Contains("PYTANIE:", req.Messages[1].Content);
        Assert.Contains("ŹRÓDŁA:", req.Messages[1].Content);
        Assert.Single(sources);
    }

    [Fact]
    public void History_inserted_between_system_and_final_message()
    {
        var history = new[] { new ChatTurn("co mówi art. 367 KPC?", "Art. 367 stanowi, że…") };

        var (req, _) = GroundedPrompt.Build("a co z § 2?", [Chunk()], history);

        Assert.Equal(4, req.Messages.Count);
        Assert.Equal(ChatRole.System, req.Messages[0].Role);
        Assert.Equal((ChatRole.User, "co mówi art. 367 KPC?"), (req.Messages[1].Role, req.Messages[1].Content));
        Assert.Equal(ChatRole.Assistant, req.Messages[2].Role);
        Assert.Equal(ChatRole.User, req.Messages[3].Role);
        Assert.Contains("a co z § 2?", req.Messages[3].Content);
        Assert.Contains("ŹRÓDŁA:", req.Messages[3].Content);
    }

    [Fact]
    public void Citation_markers_stripped_from_history_answers()
    {
        // Stare [3] odnosi się do ŹRÓDEŁ tamtej tury — skopiowane przez model przeszłoby walidację
        // anty-fabrykacji bieżącej tury, wskazując inne źródło.
        var history = new[] { new ChatTurn("pytanie", "Zgodnie z [1] oraz [12] przepis stanowi…") };

        var (req, _) = GroundedPrompt.Build("dopytanie", [Chunk()], history);

        var assistant = req.Messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.DoesNotContain("[1]", assistant.Content);
        Assert.DoesNotContain("[12]", assistant.Content);
        Assert.Contains("przepis stanowi", assistant.Content);
    }

    [Fact]
    public void Long_history_answer_is_truncated()
    {
        var longAnswer = new string('x', GroundedPrompt.MaxHistoryAnswerChars + 500);
        var history = new[] { new ChatTurn("pytanie", longAnswer) };

        var (req, _) = GroundedPrompt.Build("dopytanie", [Chunk()], history);

        var assistant = req.Messages.Single(m => m.Role == ChatRole.Assistant);
        Assert.True(assistant.Content.Length <= GroundedPrompt.MaxHistoryAnswerChars + 1); // +wielokropek
    }

    [Fact]
    public void Abstained_turn_merges_into_next_user_message()
    {
        // Tura bez odpowiedzi (abstynencja) → samotny User; scalamy z następnym User — ścisła
        // naprzemienność ról (bezpieczna dla Claude i lokalnych szablonów czatu Bielika).
        var history = new[] { new ChatTurn("pytanie bez pokrycia", null) };

        var (req, _) = GroundedPrompt.Build("nowe pytanie", [Chunk()], history);

        Assert.Equal(2, req.Messages.Count); // System + scalony User
        Assert.Equal(ChatRole.User, req.Messages[1].Role);
        Assert.Contains("pytanie bez pokrycia", req.Messages[1].Content);
        Assert.Contains("nowe pytanie", req.Messages[1].Content);
        // Brak dwóch kolejnych wiadomości tej samej roli:
        for (var i = 1; i < req.Messages.Count; i++)
            Assert.NotEqual(req.Messages[i - 1].Role, req.Messages[i].Role);
    }

    [Fact]
    public void History_capped_at_last_n_turns()
    {
        var history = Enumerable.Range(1, GroundedPrompt.HistoryTurnsTaken + 3)
            .Select(i => new ChatTurn($"pytanie {i}", $"odpowiedź {i}"))
            .ToList();

        var (req, _) = GroundedPrompt.Build("dopytanie", [Chunk()], history);

        // System + N par (User/Assistant) + finalna User.
        Assert.Equal(1 + GroundedPrompt.HistoryTurnsTaken * 2 + 1, req.Messages.Count);
        Assert.DoesNotContain(req.Messages, m => m.Content.Contains("pytanie 1")); // najstarsze wycięte
    }

    [Fact]
    public void System_prompt_contains_history_grounding_rule()
        => Assert.Contains("bieżących źródeł", GroundedPrompt.SystemPrompt);
}
