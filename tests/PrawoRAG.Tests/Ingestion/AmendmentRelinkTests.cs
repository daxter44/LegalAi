using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Ingestion.Eli;

namespace PrawoRAG.Tests.Ingestion;

/// <summary>
/// AKT-5.2 — czysta logika relinku (bez sieci/bazy): przeliczenie świeżego stanu z payloadu ELI, wykrycie
/// zmiany względem zapisanych metadanych i punktowy patch TYLKO kluczy nowel — reszta metadanych nietknięta,
/// a serializacja nowel zgodna z odczytem w TemporalAugmenter (PascalCase EliId/EffectiveDate).
/// </summary>
public class AmendmentRelinkTests
{
    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    private const string PayloadWithFreshNovela = """
    { "references": {
        "Inf. o tekście jednolitym": [ { "id": "DU/2025/383" } ],
        "Akty zmieniające": [ { "id": "DU/2026/468", "date": "2026-09-01" } ]
    } }
    """;

    [Fact]
    public void Recompute_reads_tj_and_unabsorbed_from_payload()
    {
        var (tj, unabsorbed) = AmendmentRelink.Recompute(Payload(PayloadWithFreshNovela));

        Assert.Equal("DU/2025/383", tj);
        Assert.Equal(new[] { "DU/2026/468" }, unabsorbed.Select(a => a.EliId));
    }

    [Fact]
    public void ReadStored_handles_missing_metadata()
    {
        Assert.Equal((null, 0), Map(AmendmentRelink.ReadStored(null)));
        Assert.Equal((null, 0), Map(AmendmentRelink.ReadStored(Doc("""{ "title": "Ustawa" }"""))));

        static (string?, int) Map((string? Tj, List<AmendmentRef> U) s) => (s.Tj, s.U.Count);
    }

    [Fact]
    public void NeedsUpdate_true_when_stale_store_misses_fresh_novela()
    {
        var stored = (Tj: "DU/2025/383", Unabsorbed: new List<AmendmentRef>());
        var fresh = AmendmentRelink.Recompute(Payload(PayloadWithFreshNovela));

        Assert.True(AmendmentRelink.NeedsUpdate(stored, fresh));
    }

    [Fact]
    public void NeedsUpdate_false_when_same_set_regardless_of_order()
    {
        var a = new AmendmentRef("DU/2026/1", "2026-01-01");
        var b = new AmendmentRef("DU/2026/2", null);
        var stored = (Tj: "DU/2025/383", Unabsorbed: new List<AmendmentRef> { a, b });
        var fresh = (Tj: "DU/2025/383", Unabsorbed: new List<AmendmentRef> { b, a });

        Assert.False(AmendmentRelink.NeedsUpdate(stored, fresh));
    }

    [Fact]
    public void NeedsUpdate_true_when_consolidated_text_changed()
    {
        var list = new List<AmendmentRef> { new("DU/2026/1", null) };
        Assert.True(AmendmentRelink.NeedsUpdate((Tj: "DU/2025/383", list), (Tj: "DU/2026/999", list)));
    }

    [Fact]
    public void PatchMetadata_overwrites_only_amendment_keys()
    {
        var old = Doc("""
        { "title": "Kodeks pracy", "keywords": ["praca"], "status": "obowiązujący",
          "consolidatedTextId": "DU/2023/1465", "unabsorbedAmendments": [] }
        """);
        var (tj, unabsorbed) = AmendmentRelink.Recompute(Payload(PayloadWithFreshNovela));

        var patched = AmendmentRelink.PatchMetadata(old, tj, unabsorbed);
        var root = patched.RootElement;

        // Nietknięte klucze:
        Assert.Equal("Kodeks pracy", root.GetProperty("title").GetString());
        Assert.Equal("obowiązujący", root.GetProperty("status").GetString());
        Assert.Equal("praca", root.GetProperty("keywords")[0].GetString());
        // Nadpisane klucze:
        Assert.Equal("DU/2025/383", root.GetProperty("consolidatedTextId").GetString());
        // Kontrakt z TemporalAugmenter: PascalCase EliId + odczyt przez ReadStored.
        Assert.Contains("EliId", patched.RootElement.GetProperty("unabsorbedAmendments").GetRawText());
        var reread = AmendmentRelink.ReadStored(patched);
        Assert.Equal(new[] { "DU/2026/468" }, reread.Unabsorbed.Select(a => a.EliId));
    }
}
