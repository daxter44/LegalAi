using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrawoRAG.Eval;

/// <summary>Oczekiwane zachowanie analizy dla jednej jednostki golden setu (AJ-0). Zestaw celowo
/// szerszy niż dzisiejszy <c>UnitVerdict</c> — po AJ-5 werdykty produkcyjne się do niego zbliżą;
/// do tego czasu scorer mapuje z tolerancją (patrz <see cref="AnalysisEvalScorer"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExpectedVerdict
{
    /// <summary>Klauzula zgodna z prawem — RYZYKO tutaj to fałszywy alarm.</summary>
    Ok,
    /// <summary>Wbudowana wada — oczekiwane RYZYKO; <see cref="AnalysisGoldenUnit.ExpectedEli"/> mówi,
    /// jaka norma powinna znaleźć się wśród źródeł.</summary>
    Risk,
    /// <summary>Komparycja, dane stron, przedmiot — brak twierdzenia prawnego do oceny. Akceptowane:
    /// OK / BRAK ŹRÓDEŁ / (po AJ-5) BEZ TREŚCI PRAWNEJ; RYZYKO = fałszywy alarm.</summary>
    NoLegalContent,
    /// <summary>Fragment opiera się na dokumencie poza korpusem (plan miejscowy, załącznik).</summary>
    OutOfScope,
}

/// <summary>Jedna jednostka dokumentu golden setu — klucz odpowiedzi per §. <see cref="Heading"/>
/// MUSI zgadzać się z nagłówkiem, który wyprodukuje <c>LegalUnitSplitter</c> (test pilnuje).</summary>
public sealed record AnalysisGoldenUnit
{
    public required string Heading { get; init; }
    public ExpectedVerdict ExpectedVerdict { get; init; }

    /// <summary>Norma, która powinna trafić do źródeł jednostki (metryka retrievalu niezależna od LLM).
    /// Null = nie scorujemy trafienia normy dla tej jednostki.</summary>
    public string? ExpectedEli { get; init; }
    public string? ExpectedArticle { get; init; }

    /// <summary>Opis wbudowanej wady (dla czytelności raportu i przeglądu prawnika).</summary>
    public string? PlantedRisk { get; init; }

    /// <summary>Ocena merytoryczna wymaga prawnika — jednostka nie wchodzi do recallu/fałszywych RYZYKO.</summary>
    public bool NeedsLawyer { get; init; }
}

/// <summary>Dokument golden setu analizy: tekst (strony) + polecenie użytkownika + klucz per jednostka.
/// Tekst, nie PDF — eval bada pipeline analizy, nie PdfPig.</summary>
public sealed record AnalysisGoldenDoc
{
    public required string Id { get; init; }
    /// <summary>Rodzaj dokumentu (umowa / regulamin / decyzja / pismo) — do grupowania w raporcie.</summary>
    public required string Kind { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<string> Pages { get; init; }
    public required IReadOnlyList<AnalysisGoldenUnit> Units { get; init; }

    /// <summary>Cały dokument do przeglądu prawnika (np. decyzja administracyjna) — liczymy tylko
    /// BRAK ŹRÓDEŁ i czas, nie trafność werdyktów.</summary>
    public bool NeedsLawyer { get; init; }
    public string? Note { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public const string FileName = "analysis-set.json";

    public static async Task<IReadOnlyList<AnalysisGoldenDoc>> LoadAsync(string path, CancellationToken ct = default) =>
        JsonSerializer.Deserialize<List<AnalysisGoldenDoc>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];

    /// <summary>Domyślna lokalizacja: obok binarki (kopiowana z csproj) albo w źródłach repo — jak
    /// <c>golden-set.json</c>.</summary>
    public static string DefaultPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PrawoRAG.Eval", FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return beside;
    }
}

