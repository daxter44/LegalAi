using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Tests.Fixtures;

/// <summary>Ładuje realny akt ELI (text.html + metadane) zapisany w Fixtures/Eli i buduje RawDocument (jak konektor).</summary>
public static class EliFixtures
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Eli");

    public static RawDocument LoadAct(string externalId)
    {
        var slug = externalId.Replace('/', '_');
        var html = File.ReadAllText(Path.Combine(Dir, $"act_{slug}.html"));
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, $"act_{slug}.meta.json")));
        var address = meta.RootElement.TryGetProperty("address", out var a) ? a.GetString() : null;
        return new RawDocument
        {
            Source = SourceKeys.Eli,
            ExternalId = externalId,
            DocType = DocTypes.Act,
            RawContent = html,
            SourceUrl = $"https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id={address}",
            SourcePayload = meta.RootElement.Clone(),
        };
    }
}
