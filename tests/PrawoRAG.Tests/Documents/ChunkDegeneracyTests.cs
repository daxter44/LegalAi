using PrawoRAG.Domain.Documents;

namespace PrawoRAG.Tests.Documents;

/// <summary>
/// T-JAK-0 — detektor chunków zdegenerowanych, kalibrowany na DOSŁOWNYCH wzorcach z raportu
/// odmów (Case 4/5). Precyzja ponad recall: lepiej przepuścić śmieć (reranker/model sobie poradzi)
/// niż wyciąć realny przepis czy narrację z kilkoma zanonimizowanymi nazwiskami.
/// </summary>
public class ChunkDegeneracyTests
{
    [Theory] // placeholdery tekstów jednolitych — dokładnie wzorce z Case 4/5
    [InlineData("Art. 9-21. (pominięte)")]
    [InlineData("Art. 16. (pominięty)")]
    [InlineData("Art. 58. (uchylony)")]
    [InlineData("Art. 34. (pominięty) Art. 35. (pominięty) Art. 36. (pominięty)")]
    public void Omitted_placeholders_are_degenerate(string text)
        => Assert.True(ChunkDegeneracy.IsDegenerate(text));

    [Theory] // szum anonimizacyjny SAOS — dokładnie wzorce z Case 5
    [InlineData("(...) roku, (...) z dnia (...) roku, (...) z dnia (...) roku, (...) z dnia (...) roku, (...) z dnia (...)")]
    [InlineData("kontrola operacyjna (...) pod kryptonimem: (...) kontrola operacyjna (...) pod kryptonimem: (...) kontrola operacyjna (...) pod kryptonimem: (...)")]
    public void Anonymization_noise_is_degenerate(string text)
        => Assert.True(ChunkDegeneracy.IsDegenerate(text));

    [Theory] // realna treść NIE jest wycinana
    [InlineData("Art. 4. 1. W każdym przypadku informowania o obniżeniu ceny towaru lub usługi obok informacji o obniżonej cenie uwidacznia się również informację o najniższej cenie tego towaru lub tej usługi, która obowiązywała w okresie 30 dni przed wprowadzeniem obniżki.")]
    [InlineData("Powód J. K. (...) zawarł z pozwanym umowę najmu lokalu użytkowego położonego w W. przy ulicy (...), a następnie wypowiedział ją ze skutkiem natychmiastowym z uwagi na zaległości czynszowe przekraczające dwa pełne okresy płatności.")]
    [InlineData("Art. 43. Przepis uchylony nowelizacją bywa przywoływany w uzasadnieniach — sama wzmianka o uchyleniu w zdaniu z realną treścią nie czyni chunka placeholderem.")]
    public void Substantive_content_is_not_degenerate(string text)
        => Assert.False(ChunkDegeneracy.IsDegenerate(text));

    [Fact] // narracja z kilkoma anonimizacjami w dłuższym tekście — bezpieczna (ratio, nie sama obecność)
    public void Few_anonymizations_in_long_narrative_are_safe()
    {
        var text = "Sąd ustalił, że pozwana spółka (...) prowadząca działalność deweloperską przy ulicy (...) " +
                   "w budynku wielorodzinnym (...) " +
                   string.Join(" ", Enumerable.Repeat("zrealizowała inwestycję zgodnie z projektem budowlanym oraz pozwoleniem", 10));
        Assert.False(ChunkDegeneracy.IsDegenerate(text));
    }

    [Fact] // mniej niż 3 znaczniki nigdy nie kwalifikuje jako szum (twarda podłoga)
    public void Fewer_than_three_marks_never_noise()
        => Assert.False(ChunkDegeneracy.IsAnonymizationNoise("(...) oraz (...)"));
}
