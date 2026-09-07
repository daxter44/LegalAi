using PrawoRAG.Domain;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Llm.Grounding;

namespace PrawoRAG.Tests.Grounding;

/// <summary>
/// T-DOC-2 — sekcja DOKUMENT [D1..] + WARUNKOWY system prompt (DocumentRules doklejane tylko
/// z załącznikiem; bez niego system bajt w bajt dzisiejszy — zero regresji golden-setu) —
/// oraz T-DOC-3 — walidacja przestrzeni [Dk] w CitationValidator.
/// </summary>
public class GroundedPromptDocumentTests
{
    private static RetrievedChunk Chunk(string text = "Art. 415. Kto z winy swej…") => new()
    {
        ChunkId = Guid.NewGuid(), Text = text, Source = "ELI", DocType = DocTypes.Act, Title = "Kodeks cywilny", Score = 1,
    };

    [Fact] // bez załącznika: system prompt IDENTYCZNY z dzisiejszym (twarda asercja na stałej)
    public void Without_document_system_prompt_unchanged()
    {
        var (req, _) = GroundedPrompt.Build("pytanie", [Chunk()], [], []);
        Assert.Equal(GroundedPrompt.SystemPrompt, req.Messages[0].Content);
        Assert.DoesNotContain("DOKUMENT", req.Messages[^1].Content);
    }

    [Fact] // z załącznikiem: DocumentRules doklejone do systemu, sekcja DOKUMENT w wiadomości
    public void With_document_rules_appended_and_section_present()
    {
        var (req, _) = GroundedPrompt.Build("czy §7 jest ważny?", [Chunk()], [],
            ["§7. Kara umowna wynosi 500 zł za każdy dzień zwłoki."]);

        Assert.StartsWith(GroundedPrompt.SystemPrompt, req.Messages[0].Content); // baza nietknięta
        Assert.Contains("ZAŁĄCZNIK", req.Messages[0].Content);                   // reguły doklejone

        var user = req.Messages[^1].Content;
        Assert.Contains("DOKUMENT (fragmenty załącznika", user);
        Assert.Contains("[D1] §7. Kara umowna", user);
        Assert.Contains("ŹRÓDŁA:", user); // sekcja źródeł korpusu nadal obecna, PO dokumencie
        Assert.True(user.IndexOf("DOKUMENT") < user.IndexOf("ŹRÓDŁA:"));
    }

    [Fact] // numeracja [D1..K] niezależna od [1..n] źródeł
    public void Document_numbering_independent_from_sources()
    {
        var (req, sources) = GroundedPrompt.Build("pytanie", [Chunk(), Chunk("Art. 361…")], [],
            ["fragment pierwszy", "fragment drugi"]);

        var user = req.Messages[^1].Content;
        Assert.Contains("[D1] fragment pierwszy", user);
        Assert.Contains("[D2] fragment drugi", user);
        Assert.Equal([1, 2], sources.Select(s => s.Index)); // źródła korpusu nadal od [1]
    }
}

/// <summary>T-DOC-3 — anty-fabrykacja dla przestrzeni [Dk].</summary>
public class CitationValidatorDocumentTests
{
    [Fact] // [D1] w zakresie + [1] w zakresie → czysto
    public void Mixed_in_range_citations_are_clean()
    {
        var check = CitationValidator.Validate(
            "Kara umowna z §7 [D1] podlega miarkowaniu [1].",
            ["przepis o miarkowaniu kary umownej"], 1,
            ["§7. Kara umowna wynosi 500 zł"], 1);

        Assert.True(check.IsClean);
        Assert.Equal([1], check.DocCited);
    }

    [Fact] // [D3] przy 1 fragmencie → wykryty poza zakresem, odpowiedź nieczysta
    public void Doc_marker_out_of_range_detected()
    {
        var check = CitationValidator.Validate("Zgodnie z [D3].", ["ctx"], 1, ["fragment"], 1);
        Assert.Contains(3, check.DocOutOfRange!);
        Assert.False(check.IsClean);
    }

    [Fact] // cytat artykułu OBECNEGO tylko w dokumencie (np. „art. 5 umowy") → nie jest „zmyślony"
    public void Article_present_only_in_document_not_suspicious()
    {
        var check = CitationValidator.Validate(
            "Umowa odsyła do art. 5 [D1].", ["przepis bez numeru"], 1,
            ["art. 5 umowy stanowi o karach"], 1);

        Assert.Empty(check.SuspiciousReferences);
        Assert.True(check.IsClean);
    }

    [Fact] // stary 3-argumentowy wariant: zachowanie bez zmian, pola Doc puste
    public void Legacy_overload_has_empty_doc_space()
    {
        var check = CitationValidator.Validate("Teza [1].", ["ctx"], 1);
        Assert.True(check.IsClean);
        Assert.Empty(check.DocCited!);
        Assert.Empty(check.DocOutOfRange!);
    }
}
