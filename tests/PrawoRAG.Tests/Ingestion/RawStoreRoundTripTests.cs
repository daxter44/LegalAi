using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Saos;
using PrawoRAG.Ingestion.Storage;
using PrawoRAG.Tests.Fixtures;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// Round-trip i równoważność magazynu surowych (BEZ żywej bazy). Dowodzi, że re-processing
/// z dysku jest wierny względem pobrania z sieci — fundament fazy „process". Sekcja „B" planu.
/// </summary>
public sealed class RawStoreRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "praworag-rawstore-tests", Guid.NewGuid().ToString("N"));

    private FileSystemRawDocumentStore NewStore() =>
        new(Options.Create(new RawStoreOptions { RootPath = _root }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task<List<RawDocument>> CollectAsync(IRawDocumentStore store, string source)
    {
        var list = new List<RawDocument>();
        await foreach (var d in store.EnumerateAsync(source, default)) list.Add(d);
        return list;
    }

    [Fact] // B1: surowe pola przeżywają round-trip bez zmian
    public async Task RoundTrip_preserves_raw_fields()
    {
        var raw = SaosFixtures.LoadJudgment(227221);
        var store = NewStore();
        await store.SaveAsync(raw, default);

        var back = Assert.Single(await CollectAsync(store, SourceKeys.Saos));
        Assert.Equal(raw.Source, back.Source);
        Assert.Equal(raw.ExternalId, back.ExternalId);
        Assert.Equal(raw.DocType, back.DocType);
        Assert.Equal(raw.RawContent, back.RawContent);
        Assert.Equal(raw.SourceUrl, back.SourceUrl);
        Assert.Equal(raw.SourceModificationDate, back.SourceModificationDate);
    }

    [Fact] // B2: SourcePayload (JsonElement) wierny semantycznie — normalizer dostanie identyczny JSON
    public async Task RoundTrip_preserves_source_payload()
    {
        var raw = SaosFixtures.LoadJudgment(227221);
        var store = NewStore();
        await store.SaveAsync(raw, default);
        var back = Assert.Single(await CollectAsync(store, SourceKeys.Saos));

        Assert.NotNull(raw.SourcePayload);
        Assert.NotNull(back.SourcePayload);
        var before = JsonNode.Parse(raw.SourcePayload!.Value.GetRawText());
        var after = JsonNode.Parse(back.SourcePayload!.Value.GetRawText());
        Assert.True(JsonNode.DeepEquals(before, after), "SourcePayload różni się po round-tripie");
    }

    [Fact] // B3: RDZEŃ — Normalize z magazynu == Normalize z fixture (dowód re-processingu z dysku)
    public async Task Normalization_is_equivalent_from_store_and_from_network()
    {
        var fromNetwork = SaosFixtures.LoadJudgment(227221);
        var store = NewStore();
        await store.SaveAsync(fromNetwork, default);
        var fromStore = Assert.Single(await CollectAsync(store, SourceKeys.Saos));

        var normalizer = new JudgmentNormalizer();
        var a = normalizer.Normalize(fromNetwork);
        var b = normalizer.Normalize(fromStore);

        Assert.Equal(a.Title, b.Title);
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.PlainText, b.PlainText);
        Assert.Equal(a.Locator?.CaseNumber, b.Locator?.CaseNumber);
        Assert.Equal(a.Locator?.Court, b.Locator?.Court);
        Assert.Equal(a.Locator?.JudgmentDate, b.Locator?.JudgmentDate);
        Assert.Equal(a.QualityIssues, b.QualityIssues); // kolejność i treść
        Assert.Equal(a.Segments.Count, b.Segments.Count);
        Assert.Equal(a.Segments.Select(s => s.Text), b.Segments.Select(s => s.Text));
    }

    [Fact] // B4: zapis atomowy — żadnych plików .tmp po SaveAsync, dokładnie jeden .json
    public async Task Save_is_atomic_no_tmp_leftovers()
    {
        var raw = SaosFixtures.LoadJudgment(227221);
        var store = NewStore();
        await store.SaveAsync(raw, default);

        var dir = Path.Combine(_root, SourceKeys.Saos);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(dir, "*.json"));
    }

    [Fact] // B5: sanityzacja nazwy pliku dla ELI ("DU/1997/553") — Exists/Enumerate odtwarzają ExternalId 1:1
    public async Task Eli_external_id_with_slashes_round_trips()
    {
        const string eli = "DU/1997/553";
        var raw = new RawDocument
        {
            Source = SourceKeys.Eli,
            ExternalId = eli,
            DocType = DocTypes.Act,
            RawContent = "<p>Art. 1. Test.</p>",
        };
        var store = NewStore();
        await store.SaveAsync(raw, default);

        Assert.True(await store.ExistsAsync(SourceKeys.Eli, eli, default));

        var dir = Path.Combine(_root, SourceKeys.Eli);
        var file = Assert.Single(Directory.GetFiles(dir, "*.json"));
        Assert.DoesNotContain('/', Path.GetFileName(file)); // nazwa pliku zsanityzowana

        var back = Assert.Single(await CollectAsync(store, SourceKeys.Eli));
        Assert.Equal(eli, back.ExternalId); // ExternalId odtworzony z treści, nie z nazwy pliku
    }
}
