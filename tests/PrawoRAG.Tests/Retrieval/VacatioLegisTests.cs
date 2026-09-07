using PrawoRAG.Domain.Retrieval;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-VAC — parser klauzuli wejścia w życie (funkcje czyste, bez bazy).
/// Wzorcem jest DOSŁOWNA klauzula z przypadku źródłowego (DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE
/// -2026-08-27, akt DU/2025/1847 art. 13): „z dniem 20 września 2026 r. wchodzą w życie art. 1 pkt 1
/// lit. a i c oraz pkt 3". Warunkiem dociągania jest OBECNOŚĆ wskazanych jednostek — sama fraza
/// „wchodzi w życie" nie może włączać mostu, bo stoi też w formule końcowej każdej ustawy.
/// </summary>
public class VacatioLegisTests
{
    private const string RealClause =
        "Art. 13. Ustawa wchodzi w życie po upływie 14 dni od dnia ogłoszenia, z wyjątkiem art. 1 pkt 1 "
        + "lit. a i c oraz pkt 3, które wchodzą w życie z dniem 20 września 2026 r.";

    [Fact] // Przypadek źródłowy: klauzula rozpoznana, wskazany artykuł i wszystkie jego jednostki wyłuskane.
    public void Parses_real_clause_from_the_diagnosed_case()
    {
        Assert.True(VacatioLegis.IsEntryIntoForceClause(RealClause));

        var target = Assert.Single(VacatioLegis.ParseTargets(RealClause));
        Assert.Equal("1", target.Article);
        Assert.Equal(["1", "3"], target.Points);
        Assert.Equal(["a", "c"], target.Letters);
    }

    [Fact] // Znaczniki służą do RANKOWANIA chunków artykułu (granulacji pkt/lit nie ma w lokalizatorze).
    public void Markers_cover_points_and_letters()
        => Assert.Equal(["pkt 1", "pkt 3", "lit. a", "lit. c"],
            VacatioLegis.ParseTargets(RealClause).Single().Markers().ToArray());

    [Fact] // Spójnik „i" w „lit. a i c" jest separatorem, nie literą — inaczej dostawalibyśmy „lit. i".
    public void Conjunction_is_not_mistaken_for_a_letter()
    {
        var letters = VacatioLegis.ParseTargets("wchodzą w życie art. 2 lit. a i c oraz lit. e").Single().Letters;

        Assert.Equal(["a", "c", "e"], letters);
        Assert.DoesNotContain("i", letters);
    }

    [Fact] // …ale „lit. i" jako JEDYNA litera jest legalna i musi przejść.
    public void Single_letter_i_is_kept()
        => Assert.Equal(["i"], VacatioLegis.ParseTargets("wchodzi w życie art. 4 lit. i").Single().Letters);

    [Fact] // Formuła końcowa BEZ wskazanych jednostek nie włącza mostu — nie ma czego dociągać.
    public void Plain_entry_into_force_formula_is_not_a_clause()
    {
        Assert.False(VacatioLegis.IsEntryIntoForceClause(
            "Ustawa wchodzi w życie po upływie 14 dni od dnia ogłoszenia."));
        Assert.False(VacatioLegis.IsEntryIntoForceClause(
            "Niniejsze rozporządzenie wchodzi w życie dwudziestego dnia po jego opublikowaniu."));
    }

    [Fact] // Przepis merytoryczny, który nie mówi o wejściu w życie, też nie włącza mostu.
    public void Substantive_provision_is_not_a_clause()
        => Assert.False(VacatioLegis.IsEntryIntoForceClause(
            "Art. 5. W przypadkach określonych w art. 3 pkt 2 organ wydaje decyzję w terminie 30 dni."));

    [Fact] // Kilka artykułów w jednej klauzuli — każdy z własnymi jednostkami (segmentacja po „art.").
    public void Splits_units_per_article()
    {
        var targets = VacatioLegis.ParseTargets(
            "wchodzą w życie art. 1 pkt 2, art. 5 pkt 7 lit. b oraz art. 9");

        Assert.Equal(["1", "5", "9"], targets.Select(t => t.Article).ToArray());
        Assert.Equal(["2"], targets[0].Points);
        Assert.Equal(["7"], targets[1].Points);
        Assert.Equal(["b"], targets[1].Letters);
        Assert.Empty(targets[2].Points); // „art. 9" bez uszczegółowienia = cały artykuł
    }

    [Fact] // Ten sam artykuł wskazany dwa razy scala się, żeby dociąganie nie liczyło go podwójnie.
    public void Repeated_article_is_merged()
    {
        var target = Assert.Single(VacatioLegis.ParseTargets(
            "wchodzą w życie art. 1 pkt 2 oraz art. 1 pkt 7"));

        Assert.Equal(["2", "7"], target.Points);
    }

    [Fact] // Artykuł z literą w numerze („art. 1a") — realny kształt po nowelizacjach.
    public void Handles_article_with_letter_suffix()
        => Assert.Equal("1a", VacatioLegis.ParseTargets("wchodzi w życie art. 1a pkt 3").Single().Article);

    [Fact]
    public void Empty_input_is_not_a_clause()
    {
        Assert.False(VacatioLegis.IsEntryIntoForceClause(null));
        Assert.False(VacatioLegis.IsEntryIntoForceClause("   "));
        Assert.Empty(VacatioLegis.ParseTargets(null));
    }
}
