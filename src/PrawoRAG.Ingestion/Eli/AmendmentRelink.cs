using System.Text.Json;
using System.Text.Json.Nodes;
using PrawoRAG.Domain;

namespace PrawoRAG.Ingestion.Eli;

/// <summary>
/// AKT-5.2: czysta logika relinku niewchłoniętych nowel w stanie ustalonym. Świeżo pobrana nowela trafia do
/// korpusu, ale lista <c>unabsorbedAmendments</c> aktu BAZOWEGO nie odświeża się przez fetch (skip-existing)
/// ani process (treść bez zmian → skip). Relink pobiera SAME metadane aktu, przelicza listę i — gdy się zmieniła —
/// patchuje TYLKO klucze <c>unabsorbedAmendments</c>/<c>consolidatedTextId</c> w metadanych (bez re-embeddingu).
/// Bez sieci i bez bazy — testowalne w izolacji.
/// </summary>
public static class AmendmentRelink
{
    /// <summary>Świeży stan z payloadu ELI: najnowszy t.j. + nowele ogłoszone po nim (niewchłonięte).</summary>
    public static (string? Tj, List<AmendmentRef> Unabsorbed) Recompute(JsonElement payload)
    {
        var tj = EliSejmConnector.NewestConsolidatedText(payload);
        return (tj, EliSejmConnector.ExtractUnabsorbedAmendments(payload, tj));
    }

    /// <summary>Stan zapisany w metadanych dokumentu (jsonb). Brakujące/zniekształcone klucze → wartości puste.</summary>
    public static (string? Tj, List<AmendmentRef> Unabsorbed) ReadStored(JsonDocument? meta)
    {
        if (meta is null || meta.RootElement.ValueKind != JsonValueKind.Object)
            return (null, []);
        var root = meta.RootElement;

        string? tj = root.TryGetProperty("consolidatedTextId", out var tjEl) && tjEl.ValueKind == JsonValueKind.String
            ? tjEl.GetString() : null;

        var list = new List<AmendmentRef>();
        if (root.TryGetProperty("unabsorbedAmendments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            try { list = arr.Deserialize<List<AmendmentRef>>() ?? []; } catch { list = []; }

        return (tj, list);
    }

    /// <summary>True, gdy świeży stan różni się od zapisanego (t.j. lub zbiór nowel po EliId+EffectiveDate,
    /// niezależnie od kolejności) — tylko wtedy warto pisać do bazy.</summary>
    public static bool NeedsUpdate(
        (string? Tj, List<AmendmentRef> Unabsorbed) stored,
        (string? Tj, List<AmendmentRef> Unabsorbed) fresh)
    {
        if (!string.Equals(stored.Tj, fresh.Tj, StringComparison.Ordinal)) return true;
        var s = stored.Unabsorbed.Select(a => (a.EliId, a.EffectiveDate)).ToHashSet();
        var f = fresh.Unabsorbed.Select(a => (a.EliId, a.EffectiveDate)).ToHashSet();
        return !s.SetEquals(f);
    }

    /// <summary>Zwraca kopię metadanych z nadpisanymi TYLKO kluczami <c>consolidatedTextId</c> i
    /// <c>unabsorbedAmendments</c> — reszta (title, keywords, status, …) nietknięta. Serializacja nowel
    /// domyślnymi opcjami (PascalCase) — zgodna z odczytem w <c>TemporalAugmenter</c>.</summary>
    public static JsonDocument PatchMetadata(JsonDocument? old, string? tj, List<AmendmentRef> unabsorbed)
    {
        var obj = old is not null && old.RootElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(old.RootElement.GetRawText())!.AsObject()
            : new JsonObject();

        obj["consolidatedTextId"] = tj is null ? null : JsonValue.Create(tj);
        obj["unabsorbedAmendments"] = JsonSerializer.SerializeToNode(unabsorbed);
        return JsonSerializer.SerializeToDocument(obj);
    }
}
