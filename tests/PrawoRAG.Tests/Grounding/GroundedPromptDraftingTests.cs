using PrawoRAG.Domain;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Grounding;

/// <summary>
/// Doklejka <see cref="GroundedPrompt.DraftingRules"/> (Horyzont 0 draftingu): reguły o prośbie
/// o dokument wchodzą do systemu TYLKO z flagą — bez niej prompt musi zostać bajt w bajt dzisiejszy
/// (wzorzec i uzasadnienie jak przy DocumentRules: prompty strojone pod Bielika).
/// </summary>
public class GroundedPromptDraftingTests
{
    private static RetrievedChunk Chunk() => new()
    {
        ChunkId = Guid.CreateVersion7(), DocumentId = Guid.CreateVersion7(),
        Text = "Art. 455 KC. Jeżeli termin spełnienia świadczenia nie jest oznaczony…",
        Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks cywilny",
        Score = 1.0, Similarity = 0.8,
    };

    [Fact]
    public void Flaga_dokleja_DraftingRules_do_systemu()
    {
        var (request, _) = GroundedPrompt.Build(
            "przygotuj wezwanie do zapłaty", [Chunk()], [], [], draftingRequest: true);

        var system = request.Messages[0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("PROŚBA O DOKUMENT", system.Content);
        Assert.Contains("NIE sporządzaj dokumentu", system.Content);
    }

    [Fact]
    public void Bez_flagi_system_prompt_pozostaje_dzisiejszy()
    {
        var (withoutFlag, _) = GroundedPrompt.Build("pytanie", [Chunk()], [], []);
        var (explicitFalse, _) = GroundedPrompt.Build("pytanie", [Chunk()], [], [], draftingRequest: false);

        Assert.Equal(withoutFlag.Messages[0].Content, explicitFalse.Messages[0].Content);
        Assert.DoesNotContain("PROŚBA O DOKUMENT", withoutFlag.Messages[0].Content);
    }

    [Fact]
    public void Flaga_wspolistnieje_z_regulami_zalacznika()
    {
        var (request, _) = GroundedPrompt.Build(
            "przygotuj aneks do tej umowy", [Chunk()], [], ["fragment załącznika"], draftingRequest: true);

        var system = request.Messages[0].Content;
        Assert.Contains("ZAŁĄCZNIK — zasady dodatkowe", system);
        Assert.Contains("PROŚBA O DOKUMENT", system);
    }
}
