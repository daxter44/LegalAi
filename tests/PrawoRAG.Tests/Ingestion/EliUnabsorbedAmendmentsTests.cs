using System.Text.Json;
using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Ekstrakcja nowel NIEWCHŁONIĘTYCH z „Akty zmieniające" (czysta — bez sieci). Logika przeniesiona z
/// ActNormalizer do EliSejmConnector (AKT-5.2, współdzielona z relinkiem) — testy pilnują równoważności:
/// nowela z kluczem ELI > t.j. jest niewchłonięta; ogłoszona przed t.j. (klucz ≤) — wchłonięta, pomijana.
/// </summary>
public class EliUnabsorbedAmendmentsTests
{
    private static JsonElement Meta(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Keeps_only_amendments_announced_after_consolidated_text()
    {
        var meta = Meta("""
        { "references": { "Akty zmieniające": [
            { "id": "DU/2024/100", "date": "2024-05-01" },
            { "id": "DU/2025/500", "date": "2025-06-01" },
            { "id": "DU/2026/10",  "date": "2026-01-15" }
        ] } }
        """);

        var result = EliSejmConnector.ExtractUnabsorbedAmendments(meta, "DU/2025/383");

        Assert.Equal(new[] { "DU/2025/500", "DU/2026/10" }, result.Select(a => a.EliId));
        Assert.Equal("2025-06-01", result.Single(a => a.EliId == "DU/2025/500").EffectiveDate);
    }

    [Fact] // vacatio legis (2026-09-01): z datą obwieszczenia t.j. nowela ogłoszona PRZED t.j.,
           // ale wchodząca w życie PO nim, zostaje na liście; bez daty — stara reguła (pomijana).
    public void With_tj_date_keeps_vacatio_legis_amendments()
    {
        var meta = Meta("""
        { "references": { "Akty zmieniające": [
            { "id": "DU/2025/100",  "date": "2025-03-01" },
            { "id": "DU/2025/1168", "date": "2026-01-01" }
        ] } }
        """);

        var withDate = EliSejmConnector.ExtractUnabsorbedAmendments(meta, "DU/2025/1480", new DateOnly(2025, 10, 20));
        Assert.Equal(new[] { "DU/2025/1168" }, withDate.Select(a => a.EliId));

        var withoutDate = EliSejmConnector.ExtractUnabsorbedAmendments(meta, "DU/2025/1480");
        Assert.Empty(withoutDate); // degradacja: sam klucz ELI (obie ogłoszone przed t.j.)
    }

    [Fact] // czytnik daty obwieszczenia: announcementDate przed promulgation; brak → null
    public void Publication_date_prefers_announcement_date()
    {
        Assert.Equal(new DateOnly(2025, 10, 20), EliSejmConnector.PublicationDate(
            Meta("""{ "announcementDate": "2025-10-20", "promulgation": "2025-10-28" }""")));
        Assert.Equal(new DateOnly(2025, 10, 28), EliSejmConnector.PublicationDate(
            Meta("""{ "promulgation": "2025-10-28" }""")));
        Assert.Null(EliSejmConnector.PublicationDate(Meta("""{ "title": "x" }""")));
    }

    [Fact]
    public void Empty_when_no_consolidated_text_id()
        => Assert.Empty(EliSejmConnector.ExtractUnabsorbedAmendments(
            Meta("""{ "references": { "Akty zmieniające": [ { "id": "DU/2025/500" } ] } }"""), tjId: null));

    [Fact]
    public void Empty_when_no_amending_acts_reference()
    {
        Assert.Empty(EliSejmConnector.ExtractUnabsorbedAmendments(Meta("""{ "title": "Ustawa" }"""), "DU/2025/383"));
        Assert.Empty(EliSejmConnector.ExtractUnabsorbedAmendments(
            Meta("""{ "references": { "Inf. o tekście jednolitym": [ { "id": "DU/2025/383" } ] } }"""), "DU/2025/383"));
    }

    [Fact]
    public void Skips_entries_without_or_with_unparseable_id()
    {
        var meta = Meta("""
        { "references": { "Akty zmieniające": [
            { "date": "2026-01-01" },
            { "id": "śmieci" },
            { "id": "DU/2026/1" }
        ] } }
        """);

        var result = EliSejmConnector.ExtractUnabsorbedAmendments(meta, "DU/2025/383");

        Assert.Equal(new[] { "DU/2026/1" }, result.Select(a => a.EliId));
    }
}
