using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-JAK-5b — detektor akronimów (Case 4: „KSeF"). Heurystyka formalna (≥2 wielkie litery,
/// 2–8 znaków), bez kuratorowanej listy. Wykluczenia: liczebniki rzymskie (sygnatury),
/// pytania pisane caps-lockiem.
/// </summary>
public class AcronymDetectorTests
{
    [Fact] // dosłowne pytanie z Case 4 — wykrywa KSEF, ignoruje zwykłe słowa
    public void Detects_ksef_in_case4_question()
    {
        var acronyms = AcronymDetector.Extract("Kogo obejmuje obowiązkowy KSEF w 2026 i co oznacza okres przejściowy?");
        Assert.Equal(["KSEF"], acronyms);
    }

    [Theory] // formy mieszane i klasyczne
    [InlineData("Czy KSeF obejmuje rolników?", "KSeF")]
    [InlineData("Jakie obowiązki nakłada RODO na kancelarię?", "RODO")]
    [InlineData("Ile wynosi składka ZUS przy działalności?", "ZUS")]
    public void Detects_various_acronym_forms(string question, string expected)
        => Assert.Equal([expected], AcronymDetector.Extract(question));

    [Fact] // wiele akronimów: kolejność wystąpienia, bez duplikatów (case-insensitive)
    public void Multiple_acronyms_in_order_without_duplicates()
    {
        var acronyms = AcronymDetector.Extract("Czy faktura VAT w KSeF wymaga podpisu? Bo ksef tego nie precyzuje.");
        Assert.Equal(["VAT", "KSeF"], acronyms);
    }

    [Theory] // NIE-akronimy: zwykłe zdania, początek zdania, liczebniki rzymskie w sygnaturach
    [InlineData("Czy najemca odpowiada za szkody?")]
    [InlineData("Kodeks cywilny reguluje najem.")]
    [InlineData("Co orzekł sąd w sprawie II K 13/15?")] // II = rzymski, K = 1 litera
    public void Plain_questions_yield_nothing(string question)
        => Assert.Empty(AcronymDetector.Extract(question));

    [Fact] // caps-lock guard: krzyczące pytanie ≠ żargon
    public void Caps_lock_question_disables_detection()
        => Assert.Empty(AcronymDetector.Extract("CZY TO JEST LEGALNE W POLSCE"));

    [Fact] // pusty/null → pusto
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(AcronymDetector.Extract(""));
        Assert.Empty(AcronymDetector.Extract(null));
    }
}
