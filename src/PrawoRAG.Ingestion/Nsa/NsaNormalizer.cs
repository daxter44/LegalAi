using System.Globalization;
using System.Text.Json;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;

namespace PrawoRAG.Ingestion.Nsa;

/// <summary>
/// Normalizer orzeczeń sądów administracyjnych (NSA/WSA) z datasetu JuDDGES/pl-nsa. W przeciwieństwie
/// do SAOS treść jest już PŁASKIM TEKSTEM (<c>full_text</c>, bez HTML), a metadane przychodzą
/// ustrukturyzowane w <see cref="RawDocument.SourcePayload"/> (docket_number, court_name, judgment_type,
/// finality, judgment_date, judges, keywords, extracted_legal_bases…). Selekcjonowany po
/// <see cref="DocTypes.NsaJudgment"/>, ale ZAPISYWANY jako <see cref="DocTypes.Judgment"/> — w retrievalu
/// to orzecznictwo, spójne z SAOS. Wyłącznie WYROKI (filtr na etapie fetch), więc nie odsiewamy tu typu.
/// </summary>
public sealed class NsaNormalizer : IDocumentNormalizer
{
    public string DocType => DocTypes.NsaJudgment;

    /// <summary>Znacznik początku uzasadnienia — po nim treść to motywy, przed nim sentencja/komparycja.</summary>
    private const string JustificationMarker = "UZASADNIENIE";

    public NormalizedDocument Normalize(RawDocument raw)
    {
        var issues = new List<string>();
        var text = (raw.RawContent ?? "").Trim();
        var p = raw.SourcePayload ?? default;

        var caseNumber = Str(p, "docket_number");
        var court = Str(p, "court_name");
        var judgmentType = Str(p, "judgment_type");           // np. „Wyrok NSA", „Wyrok WSA w Opolu"
        var finality = Str(p, "finality");                    // „orzeczenie prawomocne/nieprawomocne"
        var judgmentDate = ParseDate(Str(p, "judgment_date"), issues);

        if (text.Length == 0) issues.Add("Pusty full_text — brak treści do chunkowania.");

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
            ["courtType"] = CourtType(court, judgmentType), // „NSA" | „WSA" — do filtrów/analityki
            ["court"] = court,
            ["judgmentType"] = judgmentType,
            ["finality"] = finality,
            ["prawomocne"] = finality is { } f ? f.Contains("nieprawomocne", StringComparison.OrdinalIgnoreCase) ? false : f.Contains("prawomocne", StringComparison.OrdinalIgnoreCase) ? true : (bool?)null : null,
            ["caseNumber"] = caseNumber,
            ["judges"] = StrArray(p, "judges"),
            ["keywords"] = StrArray(p, "keywords"),
            ["caseTypeDescription"] = StrArray(p, "case_type_description"),
            ["challengedAuthority"] = Str(p, "challenged_authority"),
            ["referencedRegulations"] = LegalBases(p),
        };

        return new NormalizedDocument
        {
            Source = raw.Source,
            ExternalId = raw.ExternalId,
            DocType = DocTypes.Judgment, // KANONICZNY typ (orzecznictwo) — patrz DocTypes.NsaJudgment
            Title = title,
            PlainText = text,
            Segments = segments,
            Locator = locator,
            SourceUrl = raw.SourceUrl,
            SourceModificationDate = raw.SourceModificationDate,
            ContentHash = Hashing.Sha256Hex(raw.RawContent ?? ""),
            TypedMetadata = metadata,
            QualityIssues = issues,
        };
    }

    /// <summary>Sekcje: sentencja (do „UZASADNIENIE") + uzasadnienie. Brak markera → jeden segment
    /// „document". ContextHeader (sąd — sygnatura) doklejany do każdego chunka (samowystarczalność
    /// dla retrievalu i cytatu). Pusty tekst → brak segmentów (0 chunków, dokument-widmo odsiany).</summary>
    private static List<DocumentSegment> SplitSections(string text, string header, CitationLocator locator)
    {
        if (text.Length == 0) return [];

        var segs = new List<DocumentSegment>();
        void Add(string label, int start, int end)
        {
            if (start < 0 || end <= start) return;
            var slice = text[start..end].Trim();
            if (slice.Length == 0) return;
            segs.Add(new DocumentSegment
            {
                Text = slice, Kind = "section", Label = label,
                ContextHeader = string.IsNullOrWhiteSpace(header) ? null : header,
                Locator = locator, CharStart = start,
            });
        }

        var justIdx = text.IndexOf(JustificationMarker, StringComparison.Ordinal);
        if (justIdx < 0)
        {
            Add("document", 0, text.Length);
            return segs;
        }
        Add("sentencja", 0, justIdx);
        Add("uzasadnienie", justIdx, text.Length);
        return segs;
    }

    private static string BuildTitle(string? court, string? caseNumber, string? judgmentType)
    {
        var parts = new[] { judgmentType, court, caseNumber }.Where(s => !string.IsNullOrWhiteSpace(s));
        var t = string.Join(", ", parts);
        return t.Length > 0 ? t : "Orzeczenie sądu administracyjnego";
    }

    /// <summary>„NSA" gdy sąd/typ wskazują Naczelny Sąd Administracyjny, inaczej „WSA".</summary>
    private static string? CourtType(string? court, string? judgmentType)
    {
        var hay = $"{court} {judgmentType}";
        if (hay.Contains("Naczelny", StringComparison.OrdinalIgnoreCase) ||
            hay.Contains("NSA", StringComparison.Ordinal)) return "NSA";
        if (hay.Contains("Wojewódzki", StringComparison.OrdinalIgnoreCase) ||
            hay.Contains("WSA", StringComparison.Ordinal)) return "WSA";
        return null;
    }

    private static DateOnly? ParseDate(string? raw, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // JuDDGES: ISO „2005-10-13T00:00:00+02:00" albo samo „2005-10-13". Bierzemy datę KALENDARZOWĄ
        // z offsetu źródła (dto.Date) — NIE UtcDateTime, bo konwersja +02:00→UTC cofnęłaby dzień.
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return DateOnly.FromDateTime(dto.Date);
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        issues.Add($"Niezrozumiała data orzeczenia: {raw}");
        return null;
    }

    /// <summary>
    /// Podstawy prawne z <c>extracted_legal_bases</c>. UWAGA na kształt: JuDDGES trzyma OBIEKTY
    /// <c>{link, article, journal, law}</c> — inaczej niż SAOS (<c>{journalTitle, journalYear, journalNo,
    /// journalEntry, text}</c>). Zachowujemy pola źródłowe i dokładamy <c>text</c> (czytelny opis
    /// „ustawa (dziennik — artykuły)"), żeby konsument metadanych miał JEDNO wspólne pole niezależnie
    /// od źródła. Wcześniej szło to przez <see cref="StrArray"/>, które szuka wyłącznie pola „text" —
    /// dla NSA dawało zawsze pustą listę (zgubione przepisy przy każdym orzeczeniu).
    /// </summary>
    private static List<Dictionary<string, object?>> LegalBases(JsonElement p)
    {
        var list = new List<Dictionary<string, object?>>();
        if (p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("extracted_legal_bases", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            // Wariant zdegenerowany (sam tekst zamiast obiektu) — nie gubimy go.
            if (el.ValueKind == JsonValueKind.String)
            {
                if (el.GetString() is { Length: > 0 } s) list.Add(new Dictionary<string, object?> { ["text"] = s });
                continue;
            }
            if (el.ValueKind != JsonValueKind.Object) continue;

            var law = Str(el, "law");
            var journal = Str(el, "journal");
            var article = Str(el, "article");
            // Gotowe „text" (gdy źródło je poda) ma pierwszeństwo przed składanym z pól.
            var text = Str(el, "text") ?? ComposeText(law, journal, article);
            if (text is null && Str(el, "link") is null) continue; // pusty wpis — pomijamy

            list.Add(new Dictionary<string, object?>
            {
                ["law"] = law,
                ["journal"] = journal,
                ["article"] = article,
                ["link"] = Str(el, "link"),
                ["text"] = text,
            });
        }
        return list;
    }

    /// <summary>„Ustawa … (Dz.U. 2023 poz 977 — art. 14 ust. 8)" — parytet czytelności z SAOS.</summary>
    private static string? ComposeText(string? law, string? journal, string? article)
    {
        var head = law ?? journal;
        if (string.IsNullOrWhiteSpace(head)) return null;
        var detail = string.Join(" — ", new[] { law is null ? null : journal, article }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return detail.Length > 0 ? $"{head} ({detail})" : head;
    }

    // --- czytniki SourcePayload (tolerancyjne: brak pola → null/[]) ---
    private static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string[] StrArray(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var el in v.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s) list.Add(s);
            // extracted_legal_bases bywa listą obiektów {text:…} — wyłuskaj pole tekstowe.
            else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("text", out var t)
                     && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts) list.Add(ts);
        }
        return [.. list];
    }
}
