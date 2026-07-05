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
/// i <c>div.unit_para</c> (deterministycznie). Artykuł z ≥2 paragrafami dostaje SEGMENT NA PARAGRAF
/// (jeden § = jedna norma = jeden wektor — cały artykuł w jednym wektorze uśrednia kilka norm naraz
/// i rozmywa retrieval); artykuł bez §§ zostaje segmentem w całości. Nagłówek kontekstowy wbity
/// w tekst (krótka nazwa aktu + rozdział + „Art. N § M") — chunk samoopisowy dla retrievalu i cytowania.
/// Lokalizator = eli_id + artykuł + paragraf + kotwica HTML (data-id/id). Błędy → QualityIssues.
/// </summary>
public sealed class ActNormalizer : IDocumentNormalizer
{
    public string DocType => DocTypes.Act;

    private static readonly Regex ArtRe = new(@"Art\.\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);
    private static readonly Regex ParaRe = new(@"§\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);

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
            // Npgsql akceptuje dla timestamptz tylko offset 0 — wymuszamy UTC niezależnie od tego,
            // z jakim offsetem dotarła (broni też przed już zapisanymi surowymi plikami sprzed fixu w connectorze).
            SourceModificationDate = raw.SourceModificationDate?.ToUniversalTime(),
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

        var shortTitle = ShortTitle(title);

        foreach (var node in nodes)
        {
            var htmlId = NullIfEmpty(node.GetAttributeValue("id", ""));       // kotwica: „none_-chpt_XIX-arti_148"
            var dataId = NullIfEmpty(node.GetAttributeValue("data-id", ""));  // „arti_148"
            var article = ArticleNumber(node, dataId);
            var chapter = ChapterTitle(node);

            var paras = node.SelectNodes(
                ".//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_para ')]");

            if (paras is { Count: >= 2 })
            {
                // Wstęp artykułu poza §§ (rzadkie, ale kompletność > 0 utraty treści):
                // klon bez unit_para; z tekstu wypada nagłówek „Art. N." — reszta to wstęp.
                var clone = node.CloneNode(true);
                foreach (var pn in clone.SelectNodes(
                             ".//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_para ')]") ?? Enumerable.Empty<HtmlNode>())
                    pn.Remove();
                var intro = StripArticleHeading(HtmlText.ToPlainText(clone.OuterHtml));
                if (intro.Length >= 20)
                    Emit(segments, full, intro, article, paragraph: null, shortTitle, chapter,
                         eliId, displayAddress, htmlId, sourceUrl);

                foreach (var para in paras)
                {
                    var paraBody = HtmlText.ToPlainText(para.OuterHtml);
                    if (string.IsNullOrWhiteSpace(paraBody)) continue;
                    var paraId = NullIfEmpty(para.GetAttributeValue("id", ""));       // „…-arti_148-para_1"
                    var paraDataId = NullIfEmpty(para.GetAttributeValue("data-id", "")); // „para_1"
                    var paragraph = ParagraphNumber(para, paraDataId);
                    Emit(segments, full, paraBody, article, paragraph, shortTitle, chapter,
                         eliId, displayAddress, paraId ?? htmlId, sourceUrl);
                }
            }
            else
            {
                var body = HtmlText.ToPlainText(node.OuterHtml);
                if (string.IsNullOrWhiteSpace(body)) continue;
                Emit(segments, full, body, article, paragraph: null, shortTitle, chapter,
                     eliId, displayAddress, htmlId, sourceUrl);
            }
        }

        return (segments, full.ToString().TrimEnd());
    }

    private static void Emit(
        List<DocumentSegment> segments, StringBuilder full, string body,
        string? article, string? paragraph, string shortTitle, string? chapter,
        string eliId, string? displayAddress, string? anchor, string? sourceUrl)
    {
        var artLabel = article is null ? null
            : paragraph is null ? $"Art. {article}" : $"Art. {article} § {paragraph}";
        var header = string.Join(", ", new[] { shortTitle, chapter, artLabel }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = header.Length > 0 ? header + "\n" + body : body;

        var start = full.Length;
        full.Append(text).Append("\n\n");

        segments.Add(new DocumentSegment
        {
            Text = text,
            Kind = "article",
            Label = artLabel ?? "Art.",
            ContextHeader = header,
            CharStart = start,
            Locator = new CitationLocator
            {
                EliId = eliId,
                Article = article,
                Paragraph = paragraph,
                DisplayAddress = displayAddress,
                Anchor = anchor,
                SourceUrl = sourceUrl,
            },
        });
    }

    /// <summary>„Ustawa z dnia 6 czerwca 1997 r. - Kodeks karny." → „Kodeks karny" (mniej bojlerplate'u
    /// w każdym chunku = mniej rozmyty wektor); pełny tytuł zostaje w Document.Title i cytatach.</summary>
    private static string ShortTitle(string title)
    {
        var idx = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (idx < 0) idx = title.LastIndexOf(" – ", StringComparison.Ordinal);
        var tail = idx >= 0 ? title[(idx + 3)..].Trim().TrimEnd('.') : "";
        return tail.Length > 0 ? tail : title;
    }

    private static string? ParagraphNumber(HtmlNode para, string? dataId)
    {
        if (dataId is not null && dataId.StartsWith("para_", StringComparison.Ordinal))
        {
            var n = dataId["para_".Length..];
            if (n.Length > 0) return n;
        }
        var h3 = para.SelectSingleNode(".//h3");
        if (h3 is not null)
        {
            var m = ParaRe.Match(Collapse(HtmlEntity.DeEntitize(h3.InnerText)));
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Zdejmuje pierwszą linię „Art. N." z tekstu (nagłówek artykułu, nie treść wstępu).</summary>
    private static string StripArticleHeading(string text)
    {
        var trimmed = text.Trim();
        var nl = trimmed.IndexOf('\n');
        var firstLine = nl < 0 ? trimmed : trimmed[..nl];
        if (ArtRe.IsMatch(firstLine) && firstLine.Length <= 20)
            return nl < 0 ? "" : trimmed[(nl + 1)..].Trim();
        return trimmed;
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
