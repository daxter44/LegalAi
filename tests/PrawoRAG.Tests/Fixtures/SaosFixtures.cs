using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Tests.Fixtures;

/// <summary>Ładuje realne odpowiedzi SAOS zapisane w Fixtures/Saos i buduje z nich RawDocument (jak konektor).</summary>
public static class SaosFixtures
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Saos");

    public static RawDocument LoadJudgment(long id)
    {
        var path = Path.Combine(Dir, $"judgment_{id}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var data = doc.RootElement.GetProperty("data");
        var html = data.TryGetProperty("textContent", out var tc) ? tc.GetString() ?? "" : "";
        return new RawDocument
        {
            Source = SourceKeys.Saos,
            ExternalId = id.ToString(),
            DocType = DocTypes.Judgment,
            RawContent = html,
            SourceUrl = $"https://www.saos.org.pl/judgments/{id}",
            SourcePayload = data.Clone(),
        };
    }
}
