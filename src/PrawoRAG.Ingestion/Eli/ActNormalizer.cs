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
/// Normalizer aktów prawnych ELI: parsuje strukturalny <c>text.html</c> po <c>div.unit_arti</c>,
/// <c>div.unit_para</c> i <c>div.unit_pint</c> (deterministycznie), rekurencyjnie: artykuł z ≥2
/// paragrafami dzieli się na §§, paragraf z ≥2 punktami wyliczenia dzieli się na punkty. Jeden
/// wektor = jedna norma — zmierzone, że mieszanie kilku podstaw prawnych (np. art. 52 § 1 pkt 1-3
/// KP: różne, niezależne przesłanki zwolnienia) w jednym chunku rozmywa cosine o ~0.15 i zrzuca
/// przepis z top rankingu. Nagłówek kontekstowy wbity w tekst (krótka nazwa aktu + rozdział +
/// „Art. N § M pkt K") — BEZ zdania wprowadzającego paragrafu (zmierzone: obniża cosine, bo dokłada
/// bojlerplate wspólny dla wszystkich punktów) — chunk samoopisowy dla retrievalu i cytowania.
/// Lokalizator = eli_id + artykuł/paragraf/punkt + kotwica HTML (data-id/id). Błędy → QualityIssues.
/// </summary>
public sealed class ActNormalizer : IDocumentNormalizer
{
    public string DocType => DocTypes.Act;

    // Domknięcie predykatu XPath: „…i NIE jest tekstem cytowanym (pro-cite-text z preambuły obwieszczenia)".
    private const string NotCited =
        " and not(contains(concat(' ', normalize-space(@class), ' '), ' pro-cite-text '))]";

    private static readonly Regex ArtRe = new(@"Art\.\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);
    private static readonly Regex ParaRe = new(@"§\s*(\d+[a-zA-Z]*)", RegexOptions.Compiled);
    private static readonly Regex PointRe = new(@"^(\d+[a-zA-Z]*)\)", RegexOptions.Compiled);

    // „Ustawa z dnia ... r. [-] Nazwa[.]" → Nazwa (dopuszcza z/bez myślnika, z/bez kropki na końcu).
    private static readonly Regex UstawaTitleRe =
        new(@"^Ustawa z dnia .+?\d{4}\s*r\.\s*-?\s*(.+?)\.?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Obwieszczenia o tekście jednolitym: „...ogłoszenia jednolitego tekstu ustawy [-] Nazwa" → Nazwa.
    private static readonly Regex ObwieszczenieTitleRe =
        new(@"tekstu ustawy\s+(?:-\s*)?(.+?)\.?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public NormalizedDocument Normalize(RawDocument raw)
    {
        var issues = new List<string>();
        var p = raw.SourcePayload ?? default;

        var title = StringProp(p, "title") ?? raw.ExternalId;
        var displayAddress = StringProp(p, "displayAddress");
        var eliId = raw.ExternalId;

        // ELI od 2025 publikuje teksty jednolite tylko w PDF → ścieżka tekstowa; starsze/HTML → ścieżka DOM.
        var (segments, plainText) = raw.ContentFormat == ContentFormats.PdfText
            ? ActTextParser.Parse(raw.RawContent, ShortTitle(title), eliId, displayAddress, raw.SourceUrl)
            : ParseArticles(raw.RawContent, title, displayAddress, eliId, raw.SourceUrl);
        if (segments.Count == 0)
            issues.Add(raw.ContentFormat == ContentFormats.PdfText
                ? "Nie znaleziono jednostek (Art./§) w tekście PDF — sprawdź ekstrakcję/strukturę."
                : "Nie znaleziono artykułów (div.unit_arti) — sprawdź strukturę text.html.");

        DisambiguateDuplicateUnits(segments, issues);

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
        // WYKLUCZAMY „pro-cite-text": to fragmenty CYTOWANE w preambule obwieszczenia (rejestr zmian
        // przywołuje przepisy ustaw nowelizujących). Mają te same numery co prawdziwe artykuły z załącznika
        // (np. „Art. 93" = klauzula wejścia w życie obcej ustawy vs realny art. 93 aktu) → bez wykluczenia
        // fałszywa treść trafia do bazy pod numerem prawdziwego przepisu. Prawdziwy tekst = „pro-text".
        var articleNodes = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_arti ')" + NotCited);
        var shortTitle = ShortTitle(title);

        foreach (var node in articleNodes ?? Enumerable.Empty<HtmlNode>())
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
                    Emit(segments, full, intro, article, paragraph: null, point: null, shortTitle, chapter,
                         eliId, displayAddress, htmlId, sourceUrl);

                foreach (var para in paras)
                    EmitParagraph(segments, full, para, article, chapter, shortTitle, eliId, displayAddress, htmlId, sourceUrl);
            }
            else
            {
                var body = HtmlText.ToPlainText(node.OuterHtml);
                if (string.IsNullOrWhiteSpace(body)) continue;
                Emit(segments, full, body, article, paragraph: null, point: null, shortTitle, chapter,
                     eliId, displayAddress, htmlId, sourceUrl);
            }
        }

        // Akty BEZ artykułów (rozporządzenia): najwyższym poziomem jest § (unit_para), nie Art.
        // Traktujemy każdy § jako segment (z ew. podziałem na punkty) — analogicznie do § w kodeksie.
        if (segments.Count == 0)
        {
            var topParas = doc.DocumentNode.SelectNodes(
                "//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_para ')" + NotCited);
            foreach (var para in topParas ?? Enumerable.Empty<HtmlNode>())
                EmitParagraph(segments, full, para, article: null, ChapterTitle(para),
                    shortTitle, eliId, displayAddress, htmlIdFallback: null, sourceUrl);
        }

        return (segments, full.ToString().TrimEnd());
    }

    private static void Emit(
        List<DocumentSegment> segments, StringBuilder full, string body,
        string? article, string? paragraph, string? point, string shortTitle, string? chapter,
        string eliId, string? displayAddress, string? anchor, string? sourceUrl)
    {
        var artLabel = article is not null
            ? $"Art. {article}" + (paragraph is not null ? $" § {paragraph}" : "") + (point is not null ? $" pkt {point}" : "")
            : paragraph is not null
                ? $"§ {paragraph}" + (point is not null ? $" pkt {point}" : "")
                : point is not null ? $"pkt {point}" : null;
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
                Point = point,
                DisplayAddress = displayAddress,
                Anchor = anchor,
                SourceUrl = sourceUrl,
            },
        });
    }

    /// <summary>
    /// Gwarantuje unikalność lokalizatora w dokumencie. Tekst jednolity potrafi zawierać jednostkę o tym
    /// samym numerze w dwóch brzmieniach — obowiązującym i wchodzącym w życie z przyszłą datą — i OBA są
    /// prawdziwą treścią aktu (pro-text), więc filtr pro-cite-text ich nie łapie. Bez tego dwa chunki
    /// lądują pod tym samym „Art. N". Nie usuwamy (wybór „które obowiązuje" zależy od daty i bywa nietrwały):
    /// oznaczamy wariantami (etykieta + nagłówek + treść) i zgłaszamy QualityIssue do przeglądu.
    /// Działa na wyniku OBU ścieżek (HTML i PDF).
    /// </summary>
    private static void DisambiguateDuplicateUnits(List<DocumentSegment> segments, List<string> issues)
    {
        var dupes = segments
            .Select((s, i) => (s, i))
            .Where(x => x.s.Locator?.Article is not null)
            .GroupBy(x => (x.s.Locator!.Article, x.s.Locator.Paragraph, x.s.Locator.Point))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in dupes)
        {
            var members = g.ToList();
            var n = members.Count;
            issues.Add($"Duplikat jednostki „{members[0].s.Label}” — {n} wersje (np. różne brzmienie czasowe); oznaczono wariantami.");
            for (var k = 0; k < n; k++)
            {
                var (seg, idx) = members[k];
                var suffix = $" (wariant {k + 1}/{n})";
                var newHeader = (seg.ContextHeader ?? "") + suffix;
                var nl = seg.Text.IndexOf('\n');
                var newText = nl >= 0 ? newHeader + seg.Text[nl..] : newHeader + "\n" + seg.Text;
                segments[idx] = seg with { Label = (seg.Label ?? "") + suffix, ContextHeader = newHeader, Text = newText };
            }
        }
    }

    /// <summary>Emituje segment(y) dla jednego § (unit_para): jeśli ma ≥2 punkty wyliczenia — wstęp + segment
    /// na punkt; inaczej cały §. Używane i dla § w artykule (article≠null), i dla § najwyższego poziomu w
    /// rozporządzeniu (article=null).</summary>
    private static void EmitParagraph(
        List<DocumentSegment> segments, StringBuilder full, HtmlNode para,
        string? article, string? chapter, string shortTitle, string eliId, string? displayAddress,
        string? htmlIdFallback, string? sourceUrl)
    {
        var paraId = NullIfEmpty(para.GetAttributeValue("id", ""));
        var paragraph = ParagraphNumber(para, NullIfEmpty(para.GetAttributeValue("data-id", "")));

        var points = para.SelectNodes(
            ".//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_pint ')]");

        if (points is { Count: >= 2 })
        {
            // Wstęp paragrafu przed wyliczeniem jako własny segment — samodzielnie ma sens, a nie rozmywa punktów.
            var paraClone = para.CloneNode(true);
            foreach (var pn in paraClone.SelectNodes(
                         ".//div[contains(concat(' ', normalize-space(@class), ' '), ' unit_pint ')]") ?? Enumerable.Empty<HtmlNode>())
                pn.Remove();
            var paraIntro = StripParagraphHeading(HtmlText.ToPlainText(paraClone.OuterHtml));
            if (paraIntro.Length >= 20)
                Emit(segments, full, paraIntro, article, paragraph, point: null, shortTitle, chapter,
                     eliId, displayAddress, paraId ?? htmlIdFallback, sourceUrl);

            foreach (var pt in points)
            {
                // Zamierzone pominięcie zdania wprowadzającego w treści punktu (zmierzone: obniża cosine).
                var ptBody = HtmlText.ToPlainText(pt.OuterHtml);
                if (string.IsNullOrWhiteSpace(ptBody)) continue;
                var ptId = NullIfEmpty(pt.GetAttributeValue("id", ""));
                var point = PointNumber(pt, NullIfEmpty(pt.GetAttributeValue("data-id", "")));
                Emit(segments, full, ptBody, article, paragraph, point, shortTitle, chapter,
                     eliId, displayAddress, ptId ?? paraId ?? htmlIdFallback, sourceUrl);
            }
        }
        else
        {
            var paraBody = HtmlText.ToPlainText(para.OuterHtml);
            if (string.IsNullOrWhiteSpace(paraBody)) return;
            Emit(segments, full, paraBody, article, paragraph, point: null, shortTitle, chapter,
                 eliId, displayAddress, paraId ?? htmlIdFallback, sourceUrl);
        }
    }

    /// <summary>„Ustawa z dnia 6 czerwca 1997 r. - Kodeks karny." → „Kodeks karny" (mniej bojlerplate'u
    /// w każdym chunku = mniej rozmyty wektor); pełny tytuł zostaje w Document.Title i cytatach.
    /// Obsługuje też obwieszczenia o tekście jednolitym (tytuł nie zaczyna się od „Ustawa").</summary>
    private static string ShortTitle(string title)
    {
        var m = UstawaTitleRe.Match(title);
        if (m.Success && m.Groups[1].Value.Length > 0) return m.Groups[1].Value.Trim();

        var m2 = ObwieszczenieTitleRe.Match(title);
        if (m2.Success && m2.Groups[1].Value.Length > 0) return m2.Groups[1].Value.Trim();

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

    private static string? PointNumber(HtmlNode pt, string? dataId)
    {
        if (dataId is not null && dataId.StartsWith("pint_", StringComparison.Ordinal))
        {
            var n = dataId["pint_".Length..];
            if (n.Length > 0) return n;
        }
        var h3 = pt.SelectSingleNode(".//h3");
        if (h3 is not null)
        {
            var m = PointRe.Match(Collapse(HtmlEntity.DeEntitize(h3.InnerText)));
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

    /// <summary>Zdejmuje pierwszą linię „§ N." z tekstu (nagłówek paragrafu, nie treść wstępu przed punktami).</summary>
    private static string StripParagraphHeading(string text)
    {
        var trimmed = text.Trim();
        var nl = trimmed.IndexOf('\n');
        var firstLine = nl < 0 ? trimmed : trimmed[..nl];
        if (ParaRe.IsMatch(firstLine) && firstLine.Length <= 20)
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
