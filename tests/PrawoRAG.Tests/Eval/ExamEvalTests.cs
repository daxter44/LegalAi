using PrawoRAG.Eval;

namespace PrawoRAG.Tests.Eval;

/// <summary>
/// Czyste klocki evalu egzaminacyjnego (bez DB/LLM): parser litery odpowiedzi modelu,
/// parser podstawy prawnej z wykazu ministerialnego, porównanie artykułów odporne na
/// zgubione indeksy górne PDF.
/// </summary>
public sealed class ExamEvalTests
{
    [Theory]
    [InlineData("B", 'B')]
    [InlineData("B.", 'B')]
    [InlineData("  c  ", 'C')]
    [InlineData("Odpowiedź: B", 'B')]
    [InlineData("**A**", 'A')]
    [InlineData("Prawidłowa odpowiedź to C.", 'C')]      // „to" nie łapie się jako litera
    [InlineData("A, ponieważ art. 6 § 2 k.k.", 'A')]
    [InlineData("ODPOWIEDŹ", null)]                       // litery wewnątrz słowa NIE są odpowiedzią
    [InlineData("D", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseLetter_handles_model_output_variants(string? answer, char? expected) =>
        Assert.Equal(expected, ExamPrompt.ParseLetter(answer));

    [Fact]
    public void ParseLetter_takes_first_standalone_letter()
    {
        // Model czasem wylicza opcje — liczy się pierwsza samodzielna litera (deklaracja odpowiedzi).
        Assert.Equal('B', ExamPrompt.ParseLetter("B — nie A ani C"));
    }

    [Fact]
    public void QuestionBlock_contains_stem_and_all_options()
    {
        var item = new ExamItem
        {
            Nr = 1, Pytanie = "Zgodnie z Kodeksem karnym, X:", Prawidlowa = "A",
            Odpowiedzi = new() { ["A"] = "pierwsza", ["B"] = "druga", ["C"] = "trzecia" },
        };
        var block = ExamPrompt.QuestionBlock(item);
        Assert.Contains("Zgodnie z Kodeksem karnym, X:", block);
        Assert.Contains("A. pierwsza", block);
        Assert.Contains("B. druga", block);
        Assert.Contains("C. trzecia", block);
    }

    [Fact]
    public void Basis_kodeks_abbrev_parsed()
    {
        var b = ExamBasisParser.Parse("art. 6 § 2 k.k.");
        Assert.Equal("6", b.Article);
        Assert.Equal("KK", b.ActAbbrev);
        Assert.Equal("kk", b.Domain);
    }

    [Fact]
    public void Basis_kpc_not_confused_with_kc()
    {
        var b = ExamBasisParser.Parse("art. 4586 § 1 k.p.c.");
        Assert.Equal("4586", b.Article);
        Assert.Equal("kpc", b.Domain); // „k.p.c." zawiera „k.c." — kolejność dopasowania ma znaczenie
    }

    [Fact]
    public void Basis_ustawa_extracts_trigram_hint()
    {
        var b = ExamBasisParser.Parse("art. 63 ust. 3 ustawy z dnia 29 lipca 2005 r. o przeciwdziałaniu narkomanii");
        Assert.Equal("63", b.Article);
        Assert.Equal("o przeciwdziałaniu narkomanii", b.UstawaHint);
        Assert.Equal("ustawa-szczegolna", b.Domain);
    }

    [Fact]
    public void Basis_konstytucja_recognized()
    {
        var b = ExamBasisParser.Parse("art. 58 ust. 2 Konstytucji Rzeczypospolitej Polskiej");
        Assert.Equal("58", b.Article);
        Assert.Equal("konstytucja", b.Domain);
    }

    [Fact]
    public void Basis_null_or_empty_is_safe()
    {
        Assert.Equal("brak", ExamBasisParser.Parse(null).Domain);
        Assert.Equal("brak", ExamBasisParser.Parse("  ").Domain);
    }

    [Theory]
    [InlineData("631", "63(1)", true)]   // indeks górny zgubiony w PDF vs format parsera aktów
    [InlineData("631", "631", true)]
    [InlineData("43bb", "43BB", true)]   // case-insensitive
    [InlineData("63", "631", false)]     // inny artykuł to inny artykuł
    [InlineData(null, "63", false)]
    [InlineData("63", null, false)]
    public void ArticleEquals_normalizes_superscript_loss(string? a, string? b, bool expected) =>
        Assert.Equal(expected, ExamBasisParser.ArticleEquals(a, b));
}
