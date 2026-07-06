using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>Filtr odkrywania aktów ELI (czysty predykat — bez sieci). Typ + akceptowany status.
/// HTML NIE jest wymagany: connector rozwiązuje treść do najnowszego t.j. i pobiera PDF, gdy brak HTML.</summary>
public class EliDiscoveryTests
{
    private static readonly string[] Types = ["Ustawa", "Rozporządzenie"];
    private static readonly string[] Live = ["obowiązujący", "akt posiada tekst jednolity"];

    [Fact]
    public void Includes_typed_act_with_accepted_status()
    {
        Assert.True(EliSejmConnector.ShouldInclude("DU/2004/535", "Ustawa", "obowiązujący", Types, Live));
        Assert.True(EliSejmConnector.ShouldInclude("DU/2023/2824", "Rozporządzenie", "obowiązujący", Types, Live));
    }

    [Fact]
    public void Includes_act_with_consolidated_text_status()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/1997/553", "Ustawa", "akt posiada tekst jednolity", Types, Live));

    [Fact] // NOWE akty „born-PDF" (2025+, brak HTML) MUSZĄ wchodzić — connector pobierze ich PDF.
    public void Includes_pdf_only_act_without_html()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/2025/1234", "Ustawa", "obowiązujący", Types, Live));

    [Fact]
    public void Excludes_status_not_on_list()
    {
        Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", Types, Live));
        // „akt objęty tekstem jednolitym" (akt nowelizujący wchłonięty) — świadomie pomijamy.
        Assert.False(EliSejmConnector.ShouldInclude("DU/x/2", "Ustawa", "akt objęty tekstem jednolitym", Types, Live));
    }

    [Fact]
    public void Excludes_unwanted_type()
        => Assert.False(EliSejmConnector.ShouldInclude("DU/x/1", "Obwieszczenie", "obowiązujący", Types, Live));

    [Fact]
    public void Includes_any_status_when_filter_empty()
        => Assert.True(EliSejmConnector.ShouldInclude("DU/x/1", "Ustawa", "uchylony", Types, []));

    [Fact]
    public void Excludes_blank_eli()
        => Assert.False(EliSejmConnector.ShouldInclude("", "Ustawa", "obowiązujący", Types, Live));
}
