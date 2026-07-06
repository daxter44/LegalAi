using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>Filtr odkrywania aktów ELI (czysty predykat — bez sieci). Typ + akceptowany status + tekst HTML.</summary>
public class EliDiscoveryTests
{
    private static readonly string[] Types = ["Ustawa", "Rozporządzenie"];
    private static readonly string[] Live = ["obowiązujący", "akt posiada tekst jednolity"];

    [Fact]
    public void Includes_typed_act_with_accepted_status_and_html()
    {
        Assert.True(EliSejmConnector.ShouldInclude("DU/2004/535", "Ustawa", "obowiązujący", textHtml: true, Types, Live));
        Assert.True(EliSejmConnector.ShouldInclude("DU/2023/2824", "Rozporządzenie", "obowiązujący", textHtml: true, Types, Live));
    }

    [Fact]
    public void Includes_act_with_consolidated_text_status()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/1997/553", "Ustawa", "akt posiada tekst jednolity", textHtml: true, Types, Live));

    [Fact]
    public void Excludes_status_not_on_list()
    {
        Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", textHtml: true, Types, Live));
        // „akt objęty tekstem jednolitym" (akt nowelizujący wchłonięty) — świadomie pomijamy.
        Assert.False(EliSejmConnector.ShouldInclude("DU/x/2", "Ustawa", "akt objęty tekstem jednolitym", textHtml: true, Types, Live));
    }

    [Fact]
    public void Excludes_unwanted_type()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Obwieszczenie", "obowiązujący", textHtml: true, Types, Live));

    [Fact]
    public void Excludes_without_html()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "obowiązujący", textHtml: false, Types, Live));

    [Fact]
    public void Includes_any_status_when_filter_empty()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", textHtml: true, Types, []));

    [Fact]
    public void Excludes_blank_eli()
        => Assert.False(EliSejmConnector.ShouldInclude("", "Ustawa", "obowiązujący", textHtml: true, Types, Live));
}
