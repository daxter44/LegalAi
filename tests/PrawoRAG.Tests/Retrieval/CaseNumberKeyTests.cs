using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-SIG — sygnatura jako klucz strukturalny: normalizacja (kanoniczny klucz exact-match) i detekcja
/// w pytaniu. Kluczowe: wariant administracyjny z „/" („III SA/Po 154/26") MUSI być łapany — to on
/// był gubiony przez węższy regex z CitationValidator.
/// </summary>
public class CaseNumberKeyTests
{
    [Theory]
    [InlineData("III SA/Po 154/26", "III SA/PO 154/26")]
    [InlineData("  ii   aka   137/16 ", "II AKA 137/16")]   // trim + kolaps spacji + wielkie litery
    [InlineData("II FSK 1938/08", "II FSK 1938/08")]
    public void Normalize_canonicalizes(string input, string expected)
        => Assert.Equal(expected, CaseNumberKey.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_empty_is_null(string? input)
        => Assert.Null(CaseNumberKey.Normalize(input));

    [Fact] // detekcja w pytaniu naturalnym — także wariant WSA z „/", którego stary regex nie łapał
    public void Detect_finds_admin_signature_with_slash()
    {
        var keys = CaseNumberKey.Detect("Co orzekł sąd w sprawie III SA/Po 154/26 o warunki zabudowy?");
        Assert.Equal(["III SA/PO 154/26"], keys);
    }

    [Fact]
    public void Detect_finds_multiple_and_dedups_normalized()
    {
        var keys = CaseNumberKey.Detect("Porównaj II FSK 1938/08 z II FSK 1938/08 oraz I OSK 1/20.");
        Assert.Equal(["II FSK 1938/08", "I OSK 1/20"], keys);
    }

    [Fact]
    public void Detect_no_signature_is_empty()
    {
        Assert.Empty(CaseNumberKey.Detect("Jakie są przesłanki wznowienia postępowania administracyjnego?"));
        Assert.Empty(CaseNumberKey.Detect(""));
    }
}
