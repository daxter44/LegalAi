using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Saos;

namespace PrawoRAG.Ingestion.Eli;

/// <summary>
/// Normalizer aktów prawnych ELI: parsuje strukturalny <c>text.html</c> po <c>div.unit_arti</c>
/// (deterministycznie), tworząc SEGMENT NA ARTYKUŁ z nagłówkiem kontekstowym wbitym w tekst
/// (tytuł aktu + rozdział + „Art. N") — chunk jest samoopisowy dla retrievalu i cytowania.
/// Lokalizator = eli_id + numer artykułu + kotwica HTML (data-id/id). Błędy → QualityIssues.
/// </summary>
public sealed class ActNormalizer : IDocumentNormalizer
{
    public string DocType => DocTypes.Act;

    private static readonly Regex ArtRe = new(@"Art\.\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);

    public NormalizedDocument Normalize(RawDocument raw)
    {
        var issues = new List<string>();
        var p = raw.SourcePayload ?? default;

        var title = StringProp(p, "title") ?? raw.ExternalId;
        var displayAddress = StringProp(p, "displayAddress");
        var eliId = raw.ExternalId;

        var (segments, plainText) = ParseArticles(raw.RawContent, title, displayAddress, eliId, raw.SourceUrl);
        if (segments.Count == 0)
            issues.Add("Nie znaleziono artykułów (div.unit_arti) — sprawdź strukturę text.html.");

        var metadata = new Dictionary<string, object?>
        {
            ["actType"] = StringProp(p, "type"),
            ["status"] = StringProp(p, "status"),
            ["inForce"] = ResolveInForce(p),
            ["eliId"] = eliId,
            ["displayAddress"] = displayAddress,
            ["title"] = title,
            ["changeDate"] = StringProp(p, "changeDate"),
            ["keywords"] = StringArray(p, "keywordsNames") is { Length: > 0 } kn ? kn : StringArray(p, "keywords"),
        };

        return new NormalizedDocument
        {
            Source = raw.Source,
            ExternalId = raw.ExternalId,
            DocType = DocTypes.Act,
            Title = title,
            PlainText = plainText,
            Segments = segments,
            Locator = new CitationLocator { EliId = eliId, DisplayAddress = displayAddress, SourceUrl = raw.SourceUrl },
            SourceUrl = raw.SourceUrl,
            SourceModificationDate = raw.SourceModificationDate,
            ContentHash = Hashing.Sha256Hex(raw.RawContent),
            TypedMetadata = metadata,
            QualityIssues = issues,
        };
    }

    private static (List<DocumentSegment> Segments, string PlainText) ParseArticles(
        string html, string title, string? displayAddress, string eliId, string? sourceUrl)
    {
        var segments = new List<DocumentSegment>();
        var full = new StringBuilder();
        if (string.IsNullOrWhiteSpace(html)) return (segments, "");

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Artykuł = div z klasą-tokenem „unit_arti" (dopasowanie po całym tokenie, nie podłańcuchu).
        var nodes = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_arti ')]");
        if (nodes is null) return (segments, "");

        foreach (var node in nodes)
        {
            var htmlId = NullIfEmpty(node.GetAttributeValue("id", ""));       // kotwica: „none_-chpt_XIX-arti_148"
            var dataId = NullIfEmpty(node.GetAttributeValue("data-id", ""));  // „arti_148"
            var article = ArticleNumber(node, dataId);
            var body = HtmlText.ToPlainText(node.OuterHtml);
            if (string.IsNullOrWhiteSpace(body)) continue;

            var chapter = ChapterTitle(node);
            var header = string.Join(", ", new[] { title, chapter, article is not null ? $"Art. {article}" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var text = header.Length > 0 ? header + "\n" + body : body;

            var start = full.Length;
            full.Append(text).Append("\n\n");

            segments.Add(new DocumentSegment
            {
                Text = text,
                Kind = "article",
                Label = article is not null ? $"Art. {article}" : "Art.",
                ContextHeader = header,
                CharStart = start,
                Locator = new CitationLocator
                {
                    EliId = eliId,
                    Article = article,
                    DisplayAddress = displayAddress,
                    Anchor = htmlId,
                    SourceUrl = sourceUrl,
                },
            });
        }

        return (segments, full.ToString().TrimEnd());
    }

    private static string? ArticleNumber(HtmlNode node, string? dataId)
    {
        if (dataId is not null && dataId.StartsWith("arti_", StringComparison.Ordinal))
        {
            var n = dataId["arti_".Length..];
            if (n.Length > 0) return n;
        }
        var h3 = node.SelectSingleNode(".//h3");
        if (h3 is not null)
        {
            var m = ArtRe.Match(Collapse(HtmlEntity.DeEntitize(h3.InnerText)));
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Tytuł najbliższego rozdziału (ancestor div.unit_chpt) — do nagłówka kontekstowego.</summary>
    private static string? ChapterTitle(HtmlNode articleNode)
    {
        var chpt = articleNode.Ancestors("div")
            .FirstOrDefault(a => a.GetAttributeValue("class", "").Split(' ').Contains("unit_chpt"));
        var head = chpt?.SelectSingleNode(".//h3");
        return head is null ? null : Collapse(HtmlEntity.DeEntitize(head.InnerText));
    }

    private static bool? ResolveInForce(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("inForce", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.ToUpperInvariant() is "IN_FORCE" or "TRUE" or "OBOWIAZUJACY",
            _ => null,
        };
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? StringProp(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string[] StringArray(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
