using System.Text.Json;
using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Wybór najnowszego tekstu jednolitego z metadanych aktu (czysty — bez sieci). Wpisy „Inf. o tekście
/// jednolitym" mają tylko „id" (bez daty), więc najnowszy = max po (rok, pozycja) z adresu ELI.
/// To sedno aktualności: treść bierzemy z najnowszego t.j. (od 2025 często PDF), nie ze starego HTML.
/// </summary>
public class EliConsolidatedTextTests
{
    private static JsonElement Meta(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Picks_newest_by_year_then_position()
    {
        var meta = Meta("""
        { "references": { "Inf. o tekście jednolitym": [
            { "id": "DU/2022/1138" }, { "id": "DU/2025/383" }, { "id": "DU/2024/17" }
        ] } }
        """);
        Assert.Equal("DU/2025/383", EliSejmConnector.NewestConsolidatedText(meta));
    }

    [Fact]
    public void Compares_position_within_same_year()
    {
        var meta = Meta("""
        { "references": { "Inf. o tekście jednolitym": [
            { "id": "DU/2024/17" }, { "id": "DU/2024/1965" }, { "id": "DU/2024/722" }
        ] } }
        """);
        Assert.Equal("DU/2024/1965", EliSejmConnector.NewestConsolidatedText(meta));
    }

    [Fact]
    public void Returns_null_when_no_consolidated_reference()
    {
        Assert.Null(EliSejmConnector.NewestConsolidatedText(Meta("""{ "references": { "Akty zmieniające": [ { "id": "DU/2025/1" } ] } }""")));
        Assert.Null(EliSejmConnector.NewestConsolidatedText(Meta("""{ "title": "Ustawa" }""")));
    }
}
