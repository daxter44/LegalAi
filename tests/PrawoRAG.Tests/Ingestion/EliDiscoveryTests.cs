using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>Filtr odkrywania aktów ELI (czysty predykat — bez sieci). Typ + status „obowiązujący" + tekst HTML.</summary>
public class EliDiscoveryTests
{
    private static readonly string[] Types = ["Ustawa", "Rozporządzenie"];

    [Fact]
    public void Includes_in_force_typed_act_with_html()
    {
        Assert.True(EliSejmConnector.ShouldInclude("DU/2004/535", "Ustawa", "obowiązujący", textHtml: true, Types, onlyInForce: true));
        Assert.True(EliSejmConnector.ShouldInclude("DU/2023/2824", "Rozporządzenie", "obowiązujący", textHtml: true, Types, onlyInForce: true));
    }

    [Fact]
    public void Excludes_repealed_when_only_in_force()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", textHtml: true, Types, onlyInForce: true));

    [Fact]
    public void Excludes_unwanted_type()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Obwieszczenie", "obowiązujący", textHtml: true, Types, onlyInForce: true));

    [Fact]
    public void Excludes_without_html()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "obowiązujący", textHtml: false, Types, onlyInForce: true));

    [Fact]
    public void Includes_repealed_when_in_force_filter_off()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", textHtml: true, Types, onlyInForce: false));

    [Fact]
    public void Excludes_blank_eli()
        => Assert.False(EliSejmConnector.ShouldInclude("", "Ustawa", "obowiązujący", textHtml: true, Types, onlyInForce: true));
}
