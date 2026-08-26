using PrawoRAG.Ingestion.EurLex;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// T-UE-1 — warstwa SPARQL prawa UE na REALNYCH odpowiedziach CELLAR-a (fixture 2026-08-26).
/// Każdy test odpowiada pułapce zmierzonej w spike'u, a każda z nich cicho psuje korpus:
/// konsolidacja obcego aktu jako treść naszego dokumentu, wersja obowiązująca od przyszłej daty
/// jako prawo dzisiejsze, brak polskiej konsolidacji jako brak aktu, oraz filtr rocznika na
/// <c>gYear</c>, który zwraca ZERO wyników zamiast błędu.
/// </summary>
public class EurLexSparqlTests
{
    private static Dictionary<string, List<string>> Consolidations() =>
        EurLexSparql.ParsePairs(EurLexFixtures.Read(EurLexFixtures.Consolidations), "base", "cons");

    [Fact] // Parsowanie realnej odpowiedzi: obie bazy obecne, obce konsolidacje jeszcze nieodsiane.
    public void Parses_real_consolidation_response()
    {
        var map = Consolidations();

        Assert.Contains("32016R0679", map.Keys);
        Assert.Contains("32024R1689", map.Keys);
        Assert.Contains("01995L0046-20180525", map["32016R0679"]); // konsolidacja OBCEGO aktu
    }

    [Fact] // Filtr prefiksu: konsolidacja innego aktu nie może zostać treścią naszego dokumentu.
    public void Rejects_consolidations_of_other_acts()
    {
        var candidates = EurLexSparql.SelectTextCandidates(
            "32016R0679", Consolidations()["32016R0679"], new DateOnly(2026, 8, 26));

        Assert.Equal(["02016R0679-20160504", "32016R0679"], candidates);
    }

    [Fact] // Najnowsza własna konsolidacja pierwsza, tekst bazowy ostatni (fallback na realny 404).
    public void Orders_own_consolidations_newest_first_then_base()
    {
        var candidates = EurLexSparql.SelectTextCandidates(
            "32024R1689", Consolidations()["32024R1689"], new DateOnly(2026, 8, 26));

        Assert.Equal(["02024R1689-20260727", "02024R1689-20240712", "32024R1689"], candidates);
    }

    [Fact] // Wersja z datą po „dziś" to prawo, które JESZCZE nie obowiązuje — nie cytujemy go.
    public void Skips_future_dated_consolidation()
    {
        var candidates = EurLexSparql.SelectTextCandidates(
            "32024R1689", Consolidations()["32024R1689"], new DateOnly(2026, 7, 1));

        Assert.Equal(["02024R1689-20240712", "32024R1689"], candidates);
    }

    [Fact] // REACH: tekst bazowy nie ma polskiego XHTML-a, więc kolejność „konsolidacja najpierw"
           // decyduje nie tylko o aktualności, ale o tym, czy akt w ogóle wejdzie do korpusu.
    public void Reach_starts_from_consolidated_text()
    {
        var candidates = EurLexSparql.SelectTextCandidates(
            "32006R1907", Consolidations()["32006R1907"], new DateOnly(2026, 8, 26));

        Assert.StartsWith("02006R1907-", candidates[0]);
        Assert.Equal("32006R1907", candidates[^1]);
    }

    [Fact]
    public void Falls_back_to_base_text_without_consolidations()
    {
        var today = new DateOnly(2026, 8, 26);

        Assert.Equal(["32022R2065"], EurLexSparql.SelectTextCandidates("32022R2065", null, today));
        Assert.Equal(["32022R2065"], EurLexSparql.SelectTextCandidates("32022R2065", [], today));
    }

    [Fact]
    public void Parses_consolidation_date_from_celex()
    {
        Assert.Equal(new DateOnly(2026, 7, 27), EurLexSparql.ConsolidationDate("02024R1689-20260727"));
        Assert.Null(EurLexSparql.ConsolidationDate("32024R1689"));      // tekst bazowy nie ma daty wersji
        Assert.Null(EurLexSparql.ConsolidationDate("02024R1689-2026")); // ucięty sufiks
    }

    [Fact] // ZMIERZONA PUŁAPKA: cdm:resource_legal_year jest typu gYear, więc xsd:integer(?year) daje
           // zero wyników (nie błąd). Rocznik MUSI być filtrowany po CELEX-ie.
    public void Discover_query_filters_year_by_celex_not_by_year_predicate()
    {
        var query = EurLexSparql.BuildDiscoverQuery(
            new EurLexDiscoverOptions { YearFrom = 2016, YearTo = 2026 }, offset: 0);

        Assert.Contains("SUBSTR(STR(?celex), 2, 4)", query);
        Assert.DoesNotContain("xsd:integer(?year)", query);
        Assert.DoesNotContain("resource_legal_year", query);
    }

    [Fact] // „in-force" wraca jako literał „1", więc filtrujemy FILTER(?f), nie porównaniem z boolean.
    public void Discover_query_carries_scope_filters()
    {
        var query = EurLexSparql.BuildDiscoverQuery(
            new EurLexDiscoverOptions { ResourceTypes = ["REG", "DIR"], InForceOnly = true, YearFrom = 2016, YearTo = 2026, PageSize = 3000 },
            offset: 3000);

        Assert.Contains("resource-type/REG", query);
        Assert.Contains("resource-type/DIR", query);
        Assert.Contains("FILTER(?f)", query);
        Assert.DoesNotContain("\"true\"^^xsd:boolean", query);
        Assert.Contains("LIMIT 3000 OFFSET 3000", query);
    }

    [Fact]
    public void Discover_query_without_in_force_filter()
        => Assert.DoesNotContain("in-force", EurLexSparql.BuildDiscoverQuery(
            new EurLexDiscoverOptions { ResourceTypes = ["REG"], InForceOnly = false }, offset: 0));

    [Fact] // Zapytania zbiorcze: jedno na porcję aktów, nie jedno na akt (7 756 → ~150).
    public void Batch_queries_carry_all_acts()
    {
        var cons = EurLexSparql.BuildConsolidationQuery(["32016R0679", "32024R1689"]);
        var rels = EurLexSparql.BuildRelationsQuery(["32016R0679", "32024R1689"]);

        Assert.Contains("\"32016R0679\"^^xsd:string", cons);
        Assert.Contains("\"32024R1689\"^^xsd:string", cons);
        Assert.Contains("act_consolidated_consolidates_resource_legal", cons);
        Assert.Contains("resource_legal_amends_resource_legal", rels);
        Assert.Contains("resource_legal_repeals_resource_legal", rels);
    }

    [Fact] // Relacje z realnej odpowiedzi: akt zmieniający, akt uchylający, akt bez relacji.
    public void Parses_real_relations_response()
    {
        var rel = EurLexSparql.ParseRelations(EurLexFixtures.Read(EurLexFixtures.Relations));

        Assert.Contains("32005R0396", rel["32018R0070"].Amends); // rozporządzenie o pozostałościach pestycydów
        Assert.Empty(rel["32018R0070"].Repeals);
        Assert.NotEmpty(rel["32005R0080"].Repeals);              // „uchylające rozporządzenie (EWG) nr 1517/77"
        // RODO uchyla dyrektywę 95/46/WE — dlatego relacja „uchyla" NIE może odbierać aktowi treści
        // (patrz EuActClassifierTests.Repealing_predecessor_does_not_remove_text).
        Assert.Contains("31995L0046", rel["32016R0679"].Repeals);
        Assert.Empty(rel["32016R0679"].Amends);
    }

    [Fact]
    public void Parses_discover_page_and_recognizes_end_of_paging()
    {
        var page = EurLexSparql.ParseColumn(EurLexFixtures.Read(EurLexFixtures.DiscoverPage), "celex");
        var empty = EurLexSparql.ParseColumn(EurLexFixtures.Read(EurLexFixtures.EmptyPage), "celex");

        Assert.Equal(25, page.Count);
        Assert.All(page, c => Assert.StartsWith("32024", c));
        Assert.Empty(empty);
    }
}
