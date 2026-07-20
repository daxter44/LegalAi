using System.Text;
using System.Text.RegularExpressions;
using PrawoRAG.Domain.Llm;
using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Eval;

/// <summary>
/// Prompty trybu egzaminacyjnego (ABC) + parser litery odpowiedzi. CELOWO nie GroundedPrompt:
/// egzamin wymaga gołej litery (porównywalność solo vs rag), nie odpowiedzi z cytowaniami [n].
/// </summary>
public static class ExamPrompt
{
    private const string SoloSystem =
        "Jesteś ekspertem prawa polskiego. Rozwiązujesz test jednokrotnego wyboru. " +
        "Odpowiedz WYŁĄCZNIE jedną literą: A, B albo C. Bez uzasadnienia, bez dodatkowego tekstu.";

    private const string RagSystem =
        "Jesteś ekspertem prawa polskiego. Rozwiązujesz test jednokrotnego wyboru. " +
        "Poniżej masz ŹRÓDŁA (fragmenty przepisów i orzeczeń). Wybierz odpowiedź PRZEDE WSZYSTKIM " +
        "na podstawie źródeł; jeśli źródła nie rozstrzygają, wybierz najlepszą według wiedzy. " +
        "Odpowiedz WYŁĄCZNIE jedną literą: A, B albo C. Bez uzasadnienia, bez dodatkowego tekstu.";

    /// <summary>Trzon + opcje — wspólny blok pytania.</summary>
    public static string QuestionBlock(ExamItem item)
    {
        var sb = new StringBuilder(item.Pytanie).AppendLine();
        foreach (var k in new[] { "A", "B", "C" })
            sb.Append(k).Append(". ").AppendLine(item.Odpowiedzi.GetValueOrDefault(k, ""));
        return sb.ToString();
    }

    public static LlmRequest Solo(ExamItem item) => new()
    {
        Messages =
        [
            new ChatMessage(ChatRole.System, SoloSystem),
            new ChatMessage(ChatRole.User, QuestionBlock(item)),
        ],
        Temperature = 0,
        MaxTokens = 16, // goła litera — krótko i deterministycznie
    };

    public static LlmRequest Rag(ExamItem item, IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder("ŹRÓDŁA:\n");
        for (var i = 0; i < chunks.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(chunks[i].Title)
              .AppendLine(chunks[i].Text).AppendLine();
        sb.AppendLine("PYTANIE:").Append(QuestionBlock(item));
        return new LlmRequest
        {
            Messages =
            [
                new ChatMessage(ChatRole.System, RagSystem),
                new ChatMessage(ChatRole.User, sb.ToString()),
            ],
            Temperature = 0,
            MaxTokens = 16,
        };
    }

    // Samodzielna litera A/B/C — nie fragment słowa („ODPOWIEDŹ" zawiera „A", ale nie samodzielne).
    private static readonly Regex LetterRe =
        new(@"(?<![\p{L}\d])([ABC])(?![\p{L}\d])", RegexOptions.Compiled);

    /// <summary>Pierwsza samodzielna litera A/B/C z odpowiedzi modelu („B", „B.", „Odpowiedź: B", „**B**").</summary>
    public static char? ParseLetter(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var m = LetterRe.Match(answer.ToUpperInvariant());
        return m.Success ? m.Groups[1].Value[0] : null;
    }
}
