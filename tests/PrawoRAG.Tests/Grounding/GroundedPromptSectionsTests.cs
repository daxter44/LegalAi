using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Grounding;

/// <summary>
/// T-SEKCJE — podział źródeł na PRZEPISY/ORZECZNICTWO + porządek normy przed narracjami
/// (walidacja 5e: art. 415 na [1] nie wystarczył Bielikowi, gdy wizualnie ginął wśród orzeczeń).
/// Krytyczne niezmienniki: numeracja [n] ciągła i wspólna dla promptu/źródeł/walidatora
/// (porządkuje CALLER przez OrderForGrounding, Build tylko sekcjonuje); jeden typ źródeł =
/// dzisiejszy format bez nagłówków sekcji (zero regresji na golden secie).
/// </summary>
public class GroundedPromptSectionsTests
{
    private static RetrievedChunk Act(string text) => new()
    {
        ChunkId = Guid.NewGuid(), Text = text, Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks cywilny", Score = 1,
    };

    private static RetrievedChunk Judgment(string text) => new()
    {
        ChunkId = Guid.NewGuid(), Text = text, Source = "SAOS", DocType = DocTypes.Judgment, Title = "SO Testowo I C 1/24", Score = 0.5,
    };

    [Fact] // OrderForGrounding: przepisy przed orzeczeniami, stabilnie w obrębie grup
    public void Order_puts_acts_first_preserving_relative_order()
    {
        var j1 = Judgment("wyrok pierwszy"); var a1 = Act("przepis pierwszy");
        var j2 = Judgment("wyrok drugi"); var a2 = Act("przepis drugi");

        var ordered = GroundedPrompt.OrderForGrounding([j1, a1, j2, a2]);

        Assert.Equal(new[] { a1.ChunkId, a2.ChunkId, j1.ChunkId, j2.ChunkId }, ordered.Select(c => c.ChunkId));
    }

    [Fact] // Oba typy → nagłówki sekcji, numeracja ciągła przez granicę sekcji
    public void Mixed_sources_get_section_headers_with_continuous_numbering()
    {
        var chunks = GroundedPrompt.OrderForGrounding(
            [Judgment("treść wyroku"), Act("treść przepisu"), Act("treść drugiego przepisu")]);

        var (req, sources) = GroundedPrompt.Build("pytanie", chunks);
        var prompt = req.Messages[^1].Content;

        Assert.Contains("PRZEPISY:", prompt);
        Assert.Contains("ORZECZNICTWO:", prompt);
        Assert.Contains("ŹRÓDŁA:", prompt); // nagłówek główny zostaje (kompatybilność promptu)
        // Numeracja ciągła: [1],[2] przepisy, [3] orzeczenie — i sekcje w tej kolejności.
        Assert.Equal([1, 2, 3], sources.Select(s => s.Index));
        Assert.True(prompt.IndexOf("PRZEPISY:") < prompt.IndexOf("[1]"));
        Assert.True(prompt.IndexOf("[2]") < prompt.IndexOf("ORZECZNICTWO:"));
        Assert.True(prompt.IndexOf("ORZECZNICTWO:") < prompt.IndexOf("[3]"));
        // Źródła w tej samej kolejności co prompt (panel UI i walidator dostają tę samą numerację).
        Assert.Equal("Kodeks cywilny", sources[0].Title);
        Assert.Equal("SO Testowo I C 1/24", sources[2].Title);
    }

    [Fact] // Tylko orzeczenia → format jak dotąd, bez nagłówków sekcji (zero regresji)
    public void Judgments_only_keeps_flat_format()
    {
        var (req, _) = GroundedPrompt.Build("pytanie", [Judgment("a"), Judgment("b")]);
        var prompt = req.Messages[^1].Content;

        Assert.Contains("ŹRÓDŁA:", prompt);
        Assert.DoesNotContain("PRZEPISY:", prompt);
        Assert.DoesNotContain("ORZECZNICTWO:", prompt);
    }

    [Fact] // Tylko przepisy → też bez nagłówków (QU: pytanie o konkretny artykuł)
    public void Acts_only_keeps_flat_format()
    {
        var (req, _) = GroundedPrompt.Build("pytanie", [Act("a")]);
        var prompt = req.Messages[^1].Content;

        Assert.DoesNotContain("PRZEPISY:", prompt);
        Assert.DoesNotContain("ORZECZNICTWO:", prompt);
    }

    [Fact] // SystemPrompt: wymuszenie konkluzji i reguła sekcji obecne (strażnik literówek przy tuningu)
    public void System_prompt_contains_conclusion_and_section_rules()
    {
        Assert.Contains("KONKLUZJI", GroundedPrompt.SystemPrompt);
        Assert.Contains("PRZEPISY i ORZECZNICTWO", GroundedPrompt.SystemPrompt);
        Assert.Contains("Odpowiedź bez odwołań [n] jest nieprawidłowa", GroundedPrompt.SystemPrompt);
    }
}
