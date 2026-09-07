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

    [Fact] // Reguła 3 zmiękczona (odpowiedzi częściowe), ale kontrakt frazy odmowy NIETKNIĘTY:
           // UI (IsRefusal), eval odmów i golden set wykrywają odmowę przez Contains(RefusalMarker) —
           // prompt musi zawierać frazę DOSŁOWNIE i wiązać ją wyłącznie z pełną odmową.
    public void System_prompt_allows_partial_answers_but_keeps_refusal_marker_contract()
    {
        Assert.Contains("CZĘŚĆ pytania", GroundedPrompt.SystemPrompt);
        Assert.Contains($"\"{GroundedPrompt.RefusalMarker} dla tego pytania.\"", GroundedPrompt.SystemPrompt);
        Assert.Contains("ŻADNĄ część pytania", GroundedPrompt.SystemPrompt);
    }

    [Fact] // Reguła 6a (2026-09-01): przy wielu wersjach tego samego przepisu w źródłach liczby/stawki
           // cytowane z wersji NAJNOWSZEJ — siatka bezpieczeństwa syntezy obok filtra wchłoniętych nowel.
    public void System_prompt_prefers_newest_version_of_same_provision()
    {
        Assert.Contains("KILKU wersjach", GroundedPrompt.SystemPrompt);
        Assert.Contains("z wersji najnowszej", GroundedPrompt.SystemPrompt);
        Assert.Contains("kontekst historyczny", GroundedPrompt.SystemPrompt);
    }

    /// <summary>ODM-4: kanoniczna definicja odmowy treściowej — fraza (bieżąca lub legacy) BEZ cytowań
    /// [n]. Odpowiedź MIESZANA (fraza + treść z [n]) to NIE odmowa: klasyfikowanie jej jako odmowy
    /// chowało panel źródeł przy żywych linkach [n] i zawyżało metrykę (złapane żywcem 2026-09-01).</summary>
    [Theory]
    [InlineData("Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania.", true)]
    [InlineData("Nie znalazłem jednoznacznej podstawy prawnej dla tego pytania. Zawęź pytanie lub wskaż konkretny akt/sygnaturę.", true)]
    [InlineData("Nie mam wystarczających źródeł, aby odpowiedzieć.", true)] // legacy w zapisanych rozmowach
    [InlineData("Nie znalazłem jednoznacznej podstawy prawnej, ale za nadgodziny przysługuje dodatek 50% [22].", false)] // mieszana
    [InlineData("Za nadgodziny przysługuje dodatek 100% albo 50% [1, 2].", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Content_refusal_requires_marker_without_citations(string? answer, bool expected) =>
        Assert.Equal(expected, GroundedPrompt.IsContentRefusal(answer));

    [Fact] // ODM-6: reguła 3 zakazuje wprost łączenia frazy odmowy z odpowiedzią/cytowaniami.
    public void System_prompt_forbids_mixing_refusal_with_answer() =>
        Assert.Contains("NIGDY nie łącz", GroundedPrompt.SystemPrompt);

    [Fact] // ODM-1/3: reguła 6 promptu smalltalk cytuje DOSŁOWNIE zdanie zastępcze serwera, a samo
           // zdanie nie zawiera frazy odmowy — odmowa „to nie prawo" NIE liczy się do metryki odmów.
    public void Smalltalk_out_of_scope_message_is_consistent_and_not_a_content_refusal()
    {
        Assert.Contains(SmalltalkPrompt.OutOfScopeMessage, SmalltalkPrompt.SystemPrompt);
        Assert.False(GroundedPrompt.IsContentRefusal(SmalltalkPrompt.OutOfScopeMessage));
    }
}
