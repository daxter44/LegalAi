using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>Mapa skrótów kodeksów → kanoniczna nazwa (QU-2, czysta). Rozróżnia KK/KKW, ignoruje wielkość liter.</summary>
public class ActAliasesTests
{
    [Theory]
    [InlineData("KW", "Kodeks wykroczeń")]
    [InlineData("kpc", "Kodeks postępowania cywilnego")]
    [InlineData("KK", "Kodeks karny")]
    [InlineData("KKW", "Kodeks karny wykonawczy")]   // nie mylone z KK
    public void Maps_known_abbreviations(string abbr, string canonical)
        => Assert.Equal(canonical, ActAliases.Canonical(abbr));

    [Theory]
    [InlineData("kodeks wykroczeń")]   // fraza, nie skrót → resolver użyje pg_trgm
    [InlineData("ustawa o VAT")]
    [InlineData(null)]
    public void Returns_null_for_non_abbreviations(string? hint)
        => Assert.Null(ActAliases.Canonical(hint));
}
