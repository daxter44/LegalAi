using System.Text;
using System.Text.Json;

namespace PrawoRAG.Ingestion.EurLex;

/// <summary>
/// Warstwa SPARQL CELLAR-a — BEZ sieci: budowa zapytań, parsowanie odpowiedzi i wybór wersji tekstu.
/// Wydzielona, bo tu siedzą pułapki USTALONE POMIAREM (2026-08-26), a każda z nich cicho psuje korpus:
/// <list type="number">
/// <item><c>resource_legal_year</c> jest typu <c>xsd:gYear</c> — rzutowanie <c>xsd:integer(?year)</c>
/// daje ZERO wyników (nie błąd!). Rocznik filtrujemy po CELEX-ie: <c>SUBSTR(STR(?celex), 2, 4)</c>.</item>
/// <item><c>in-force</c> wraca jako literał „1", więc porównanie z <c>"true"^^xsd:boolean</c> też daje
/// zero — używamy <c>FILTER(?f)</c>.</item>
/// <item>zapytanie o konsolidacje zwraca też konsolidacje OBCYCH aktów (akt nowelizujący „konsoliduje"
/// cudze teksty) → filtr po prefiksie CELEX-u.</item>
/// <item>konsolidacje bywają datowane w PRZYSZŁOŚĆ → bierzemy tylko datę &lt;= dziś.</item>
/// <item>nie każda konsolidacja istnieje po polsku (realny 404) → wynikiem jest LISTA kandydatów
/// z tekstem bazowym na końcu, nie jedna wartość.</item>
/// </list>
/// </summary>
public static class EurLexSparql
{
    private const string Prefixes = """
        PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
        PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
        """;

    private const string ResourceTypeBase = "http://publications.europa.eu/resource/authority/resource-type/";

    /// <summary>
    /// Zapytanie odkrywające zakres: CELEX-y aktów danego typu, opcjonalnie tylko obowiązujące,
    /// w widełkach roczników. Stronicowane (<see cref="EurLexDiscoverOptions.PageSize"/>).
    /// </summary>
    public static string BuildDiscoverQuery(EurLexDiscoverOptions d, int offset)
    {
        var types = string.Join(" ", d.ResourceTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => $"<{ResourceTypeBase}{t.Trim()}>"));

        var sb = new StringBuilder();
        sb.AppendLine(Prefixes);
        sb.AppendLine("SELECT DISTINCT ?celex WHERE {");
        sb.AppendLine($"  VALUES ?rt {{ {types} }}");
        sb.AppendLine("  ?w cdm:work_has_resource-type ?rt ; cdm:resource_legal_id_celex ?celex .");
        if (d.InForceOnly)
        {
            sb.AppendLine("  ?w cdm:resource_legal_in-force ?f .");
            sb.AppendLine("  FILTER(?f)");
        }
        // Rocznik z CELEX-u, NIE z cdm:resource_legal_year (gYear → xsd:integer daje 0 wyników).
        sb.AppendLine($"  FILTER(xsd:integer(SUBSTR(STR(?celex), 2, 4)) >= {d.YearFrom}"
            + $" && xsd:integer(SUBSTR(STR(?celex), 2, 4)) <= {d.YearTo})");
        sb.AppendLine("}");
        sb.AppendLine($"ORDER BY DESC(?celex) LIMIT {d.PageSize} OFFSET {offset}");
        return sb.ToString();
    }

    /// <summary>Zbiorcze zapytanie o wersje skonsolidowane PORCJI aktów (zamiast jednego na akt).
    /// Zwraca pary (akt bazowy, CELEX konsolidacji) — filtrowanie robi <see cref="SelectTextCandidates"/>.</summary>
    public static string BuildConsolidationQuery(IEnumerable<string> baseCelexes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Prefixes);
        sb.AppendLine("SELECT ?base ?cons WHERE {");
        sb.AppendLine($"  VALUES ?base {{ {Values(baseCelexes)} }}");
        sb.AppendLine("  ?bw cdm:resource_legal_id_celex ?base .");
        sb.AppendLine("  ?cw cdm:act_consolidated_consolidates_resource_legal ?bw ;");
        sb.AppendLine("      cdm:resource_legal_id_celex ?cons .");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Zbiorcze zapytanie o relacje aktu: co zmienia i co uchyla. Podstawa klasyfikacji
    /// (<see cref="EuActClassifier"/>), czyli decyzji „chunkować czy tylko metadane".</summary>
    public static string BuildRelationsQuery(IEnumerable<string> baseCelexes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Prefixes);
        sb.AppendLine("SELECT ?base ?rel ?target WHERE {");
        sb.AppendLine($"  VALUES ?base {{ {Values(baseCelexes)} }}");
        sb.AppendLine("  ?bw cdm:resource_legal_id_celex ?base .");
        sb.AppendLine("  { ?bw cdm:resource_legal_amends_resource_legal ?tw . BIND(\"amends\" AS ?rel) }");
        sb.AppendLine("  UNION");
        sb.AppendLine("  { ?bw cdm:resource_legal_repeals_resource_legal ?tw . BIND(\"repeals\" AS ?rel) }");
        sb.AppendLine("  ?tw cdm:resource_legal_id_celex ?target .");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Zbiorcze zapytanie o POLSKIE tytuły aktów — tytuł rozstrzyga, czy akt jest czysto
    /// nowelizujący (patrz <see cref="EuActClassifier.IsPureAmendment"/>), a przy okazji jest metadaną
    /// dokumentu. Zmierzone: 6 760 z 7 756 obowiązujących rozporządzeń i dyrektyw ma tytuł polski —
    /// brak tytułu PL jest dobrym przybliżeniem braku polskiego tekstu.</summary>
    public static string BuildTitleQuery(IEnumerable<string> baseCelexes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Prefixes);
        sb.AppendLine("SELECT ?base ?title WHERE {");
        sb.AppendLine($"  VALUES ?base {{ {Values(baseCelexes)} }}");
        sb.AppendLine("  ?bw cdm:resource_legal_id_celex ?base .");
        sb.AppendLine("  ?e cdm:expression_belongs_to_work ?bw ;");
        sb.AppendLine("     cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/POL> ;");
        sb.AppendLine("     cdm:expression_title ?title .");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Values(IEnumerable<string> celexes) => string.Join(" ", celexes
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => $"\"{c.Trim()}\"^^xsd:string"));

    /// <summary>Wartości jednej zmiennej z odpowiedzi SPARQL JSON. Pusta lista, gdy brak wyników.</summary>
    public static List<string> ParseColumn(string json, string variable)
    {
        var result = new List<string>();
        foreach (var row in Bindings(json))
            if (Value(row, variable) is { } v) result.Add(v);
        return result;
    }

    /// <summary>Pary (klucz → wartości) z dwukolumnowej odpowiedzi. Klucze case-insensitive.</summary>
    public static Dictionary<string, List<string>> ParsePairs(string json, string keyVar, string valueVar)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Bindings(json))
        {
            if (Value(row, keyVar) is not { } key || Value(row, valueVar) is not { } val) continue;
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(val, StringComparer.OrdinalIgnoreCase)) list.Add(val);
        }
        return map;
    }

    /// <summary>Relacje aktu z odpowiedzi <see cref="BuildRelationsQuery"/>: CELEX → (co zmienia, co uchyla).</summary>
    public static Dictionary<string, EuActRelations> ParseRelations(string json)
    {
        var amends = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var repeals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Bindings(json))
        {
            if (Value(row, "base") is not { } key || Value(row, "target") is not { } target) continue;
            var bucket = Value(row, "rel") switch
            {
                "amends" => amends,
                "repeals" => repeals,
                _ => null,
            };
            if (bucket is null) continue;
            if (!bucket.TryGetValue(key, out var list)) bucket[key] = list = [];
            if (!list.Contains(target, StringComparer.OrdinalIgnoreCase)) list.Add(target);
        }

        var keys = amends.Keys.Concat(repeals.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        return keys.ToDictionary(
            k => k,
            k => new EuActRelations(
                amends.GetValueOrDefault(k) ?? [],
                repeals.GetValueOrDefault(k) ?? []),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kolejność prób pobrania tekstu: najnowsza WŁASNA konsolidacja z datą &lt;= <paramref name="today"/>,
    /// potem starsze malejąco, na końcu CELEX bazowy. Odsiewa konsolidacje obcych aktów (prefiks)
    /// i wersje przyszłe (data). Kolejność ma podwójne uzasadnienie: aktualność (konsolidacja to prawo
    /// dziś obowiązujące) ORAZ pokrycie (dla części starszych aktów tekst bazowy nie ma polskiej wersji,
    /// a skonsolidowany ma — zmierzone na REACH i e-Privacy).
    /// </summary>
    public static List<string> SelectTextCandidates(string baseCelex, IEnumerable<string>? consolidations, DateOnly today)
    {
        var candidates = new List<string>();
        var prefix = ConsolidatedPrefix(baseCelex);

        if (prefix is not null && consolidations is not null)
        {
            var own = consolidations
                .Where(c => c is not null && c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(c => (Celex: c, Date: ConsolidationDate(c)))
                .Where(x => x.Date is { } d && d <= today)
                .OrderByDescending(x => x.Date!.Value)
                .ThenByDescending(x => x.Celex, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Celex);
            candidates.AddRange(own.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        candidates.Add(baseCelex);
        return candidates;
    }

    /// <summary>„32016R0679" → „02016R0679-" (prefiks CELEX-u konsolidacji TEGO aktu). Null dla
    /// identyfikatora w nieoczekiwanym kształcie.</summary>
    public static string? ConsolidatedPrefix(string baseCelex) =>
        string.IsNullOrWhiteSpace(baseCelex) || baseCelex.Trim().Length < 6 ? null : "0" + baseCelex.Trim()[1..] + "-";

    /// <summary>Data wersji z CELEX-u konsolidacji („02024R1689-20260727" → 2026-07-27); null, gdy brak.</summary>
    public static DateOnly? ConsolidationDate(string consolidatedCelex)
    {
        var dash = consolidatedCelex.LastIndexOf('-');
        if (dash < 0 || consolidatedCelex.Length - dash - 1 != 8) return null;
        var span = consolidatedCelex.AsSpan(dash + 1);
        return int.TryParse(span[..4], out var y) && int.TryParse(span[4..6], out var m) && int.TryParse(span[6..8], out var d)
            && y is >= 1950 and <= 2200 && m is >= 1 and <= 12 && d is >= 1 and <= 31
            ? new DateOnly(y, m, d)
            : null;
    }

    private static IEnumerable<JsonElement> Bindings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
            yield break;

        // Materializujemy, bo JsonDocument jest zwalniany przy wyjściu z metody.
        foreach (var row in bindings.EnumerateArray().ToList()) yield return row.Clone();
    }

    private static string? Value(JsonElement row, string name) =>
        row.TryGetProperty(name, out var cell) && cell.TryGetProperty("value", out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
