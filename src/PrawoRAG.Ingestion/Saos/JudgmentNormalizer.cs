using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.Saos;

/// <summary>
/// Normalizer orzeczeń SAOS: HTML→tekst, detekcja sekcji (komparycja/sentencja/uzasadnienie),
/// ekstrakcja i walidacja metadanych (sygnatura, sąd, data, podstawy prawne). Nie odrzuca dokumentu
/// przy błędach danych — zgłasza je w QualityIssues. Specyfikacja: plan, „Zweryfikowana struktura źródeł".
/// </summary>
public sealed partial class JudgmentNormalizer : IDocumentNormalizer
{
    public string DocType => DocTypes.Judgment;

    private static readonly string[] SentenceMarkers = ["WYROK", "POSTANOWIENIE", "UCHWAŁA", "ZARZĄDZENIE"];
    private const string JustificationMarker = "UZASADNIENIE";

    // Formularz uzasadnienia (obowiązkowy dla apelacji karnych od 2019, art. 99a k.p.k.) powtarza dla
    // KAŻDEGO zarzutu identyczny szablon: checkbox (☐/☒) + stałe etykiety rubryk. Bez czyszczenia, przy
    // apelacji z wieloma zarzutami, chunk może wylądować głównie na powtórzeniach szablonu — zero treści
    // specyficznej dla sprawy. Zmierzone na M4: taki chunk (Sąd Apelacyjny w Gdańsku, II AKa 11/23) dostawał
    // sztucznie podniesiony cosine (0,78) do zupełnie niezwiązanego pytania — powtarzalna struktura tworzy
    // wektor „uśredniony". Usuwamy WYŁĄCZNIE jednoznaczny bojlerplate (linie z samym checkboxem, stałe
    // nagłówki rubryk, filler „Nie dotyczy") — nigdy właściwą treść uzasadnienia (zarzuty, rozumowanie sądu).
    //
    // Etykiety rubryk odmieniają się przez liczbę/rodzaj („zarzutu"/„zarzutów", „zasadny"/„zasadne",
    // „uznania dowodu"/„nieuwzględnienia dowodu"…) — dopasowanie po PREFIKSIE, nie sztywnej liście
    // (zmierzone na realnym wyroku wielowątkowym: cztery różne odmiany „Zwięźle o powodach…" w jednym dokumencie).
    private static readonly string[] FormularzLabelPrefixes =
    [
        "Zwięźle o powodach",
        "STANOWISKO SĄDU ODWOŁAWCZEGO",
    ];

    public NormalizedDocument Normalize(RawDocument raw)
    {
        var issues = new List<string>();
        var text = HtmlText.ToPlainText(raw.RawContent);
        var p = raw.SourcePayload ?? default;

        var caseNumber = FirstCaseNumber(p);
        var court = StringAt(p, "division", "court", "name");
        var courtType = StringProp(p, "courtType");
        var judgmentType = StringProp(p, "judgmentType");
        var judgmentDate = ResolveJudgmentDate(p, text, issues);

        var locator = new CitationLocator
        {
            CaseNumber = caseNumber,
            Court = court,
            JudgmentDate = judgmentDate,
            SourceUrl = raw.SourceUrl,
        };

        var title = BuildTitle(court, caseNumber, judgmentType);
        var header = string.Join(" — ", new[] { court, caseNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var segments = SplitSections(text, header, locator);

        var metadata = new Dictionary<string, object?>
        {
            ["courtType"] = courtType,
            ["court"] = court,
            ["division"] = StringAt(p, "division", "name"),
            ["judgmentType"] = judgmentType,
            ["caseNumber"] = caseNumber,
            ["judges"] = JudgeNames(p),
            ["keywords"] = StringArray(p, "keywords"),
            ["referencedRegulations"] = ReferencedRegulations(p),
            ["publicationDate"] = StringAt(p, "source", "publicationDate"),
        };

        return new NormalizedDocument
        {
            Source = raw.Source,
            ExternalId = raw.ExternalId,
            DocType = DocTypes.Judgment,
            Title = title,
            PlainText = text,
            Segments = segments,
            Locator = locator,
            SourceUrl = raw.SourceUrl,
            SourceModificationDate = raw.SourceModificationDate,
            ContentHash = Hashing.Sha256Hex(raw.RawContent),
            TypedMetadata = metadata,
            QualityIssues = issues,
        };
    }

    // --- Sekcje ---
    private static List<DocumentSegment> SplitSections(string text, string header, CitationLocator locator)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        int sentenceIdx = SentenceMarkers
            .Select(m => text.IndexOf(m, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        int justIdx = text.IndexOf(JustificationMarker, StringComparison.Ordinal);

        var segs = new List<DocumentSegment>();
        void Add(string label, int start, int end)
        {
            if (start < 0 || end <= start) return;
            var slice = text[start..end].Trim();
            if (label == "uzasadnienie") slice = StripFormularzBoilerplate(slice);
            if (slice.Length == 0) return;
            segs.Add(new DocumentSegment
            {
                Text = slice, Kind = "section", Label = label,
                ContextHeader = header, Locator = locator, CharStart = start,
            });
        }

        if (sentenceIdx < 0 && justIdx < 0)
        {
            Add("document", 0, text.Length);
            return segs;
        }

        int sentenceEnd = justIdx > sentenceIdx && justIdx > 0 ? justIdx : text.Length;
        if (sentenceIdx > 0) Add("komparycja", 0, sentenceIdx);
        if (sentenceIdx >= 0) Add("sentencja", sentenceIdx, sentenceEnd);
        if (justIdx >= 0) Add("uzasadnienie", justIdx, text.Length);
        return segs;
    }

    /// <summary>Usuwa linie formularza uzasadnienia (checkbox + stałe etykiety rubryk + filler „Nie dotyczy")
    /// z sekcji „uzasadnienie". Zostawia właściwą treść (zarzuty, rozumowanie sądu) bez zmian.</summary>
    private static string StripFormularzBoilerplate(string text)
    {
        var kept = text.Split('\n')
            .Where(line =>
            {
                var t = line.Trim();
                if (CheckboxLineRegex().IsMatch(line.TrimStart())) return false;
                if (t.Equals("Nie dotyczy", StringComparison.OrdinalIgnoreCase)) return false;
                return !FormularzLabelPrefixes.Any(l => t.StartsWith(l, StringComparison.OrdinalIgnoreCase));
            });
        return MultiBlankRegex().Replace(string.Join('\n', kept), "\n\n").Trim();
    }

    // --- Data: walidacja + fallback z treści ---
    private static DateOnly? ResolveJudgmentDate(JsonElement p, string text, List<string> issues)
    {
        var raw = StringProp(p, "judgmentDate");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var d) && d.Year >= 1990 && d <= today)
            return d;

        if (!string.IsNullOrEmpty(raw))
            issues.Add($"judgmentDate poza zakresem lub nieparsowalna: '{raw}'");

        var fromText = ExtractDateFromText(text);
        if (fromText is { } ft && ft.Year >= 1990 && ft <= today)
        {
            issues.Add($"judgmentDate skorygowana z treści: {ft:yyyy-MM-dd}");
            return ft;
        }
        return null;
    }

    private static DateOnly? ExtractDateFromText(string text)
    {
        var m = DniaRegex().Match(text);
        if (!m.Success) return null;
        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = PolishMonth(m.Groups[2].Value);
        var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (month == 0) return null;
        try { return new DateOnly(year, month, day); } catch { return null; }
    }

    private static int PolishMonth(string name) => name.ToLowerInvariant() switch
    {
        "stycznia" => 1, "lutego" => 2, "marca" => 3, "kwietnia" => 4, "maja" => 5, "czerwca" => 6,
        "lipca" => 7, "sierpnia" => 8, "września" => 9, "października" => 10, "listopada" => 11, "grudnia" => 12,
        _ => 0,
    };

    // --- Metadane: helpery JSON ---
    private static string? FirstCaseNumber(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("courtCases", out var cc) &&
        cc.ValueKind == JsonValueKind.Array && cc.GetArrayLength() > 0 &&
        cc[0].TryGetProperty("caseNumber", out var n) ? n.GetString() : null;

    private static string[] JudgeNames(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("judges", out var j) && j.ValueKind == JsonValueKind.Array
            ? j.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
            : [];

    private static List<Dictionary<string, object?>> ReferencedRegulations(JsonElement p)
    {
        var list = new List<Dictionary<string, object?>>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("referencedRegulations", out var rr) ||
            rr.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in rr.EnumerateArray())
        {
            list.Add(new Dictionary<string, object?>
            {
                ["journalTitle"] = Str(r, "journalTitle"),
                ["journalYear"] = Int(r, "journalYear"),
                ["journalNo"] = Int(r, "journalNo"),
                ["journalEntry"] = Int(r, "journalEntry"),
                ["text"] = Str(r, "text"),
            });
        }
        return list;

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }

    private static string[] StringArray(JsonElement p, string prop) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    private static string? StringProp(JsonElement p, string prop) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? StringAt(JsonElement p, params string[] path)
    {
        var cur = p;
        foreach (var seg in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    private static string BuildTitle(string? court, string? caseNumber, string? judgmentType)
    {
        var parts = new[] { judgmentType, court, caseNumber }.Where(s => !string.IsNullOrWhiteSpace(s));
        var title = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(title) ? "Orzeczenie" : title;
    }

    [GeneratedRegex(@"[Dd]nia\s+(\d{1,2})\s+([a-ząćęłńóśźż]+)\s+(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex DniaRegex();

    [GeneratedRegex(@"^[☐☒]\s*.*$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckboxLineRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiBlankRegex();
}
