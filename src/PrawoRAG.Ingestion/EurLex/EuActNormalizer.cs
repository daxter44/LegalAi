using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Sources;
using PrawoRAG.Ingestion.Saos;

namespace PrawoRAG.Ingestion.EurLex;

/// <summary>
/// Normalizer aktów prawa UE z CELLAR-a (Faza 3). Selektor <see cref="DocTypes.EuAct"/>, kanonicznie
/// zapisywany jako <see cref="DocTypes.Act"/> — w retrievalu to akt prawny, spójnie z ISAP (wzorem NSA,
/// gdzie „nsa-judgment" zapisuje się jako „judgment").
///
/// Trzy tory parsowania, wybierane po ZAWARTOŚCI dokumentu, nie po roczniku ani klasie aktu
/// (pomiar 2026-08-26 na losowej próbce 20 aktów): 45% dokumentów ma kotwice <c>id="art_N"</c>,
/// 30% to markup „legacy" ze starszych konwerterów CELLAR-a BEZ żadnych identyfikatorów struktury
/// (jest tylko tekst „Artykuł N"), a reszta nie ma polskiego XHTML-a wcale.
///
/// Chunki powstają WYŁĄCZNIE z białej listy jednostek (artykuły + załączniki). Uzasadnienie: widzieliśmy
/// już cztery wersje konwertera i dwa różne warianty markupu, a czarna lista śmieci przy zmianie schematu
/// CICHO przepuszcza nowy śmieć, biała lista GŁOŚNO pada (0 jednostek → <see cref="NormalizedDocument.QualityIssues"/>).
/// </summary>
public sealed class EuActNormalizer : IDocumentNormalizer
{
    /// <summary>Krótkie nazwy zwyczajowe do nagłówka kontekstowego chunka — „RODO" jest tym, czym operuje
    /// prawnik, a „rozporządzenie Parlamentu Europejskiego i Rady (UE) 2016/679" bojlerplate'em powtórzonym
    /// w każdym chunku (ta sama zasada, co <c>ShortTitle</c> w ActNormalizer dla ISAP-u).</summary>
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["32016R0679"] = "RODO",
        ["32024R1689"] = "AI Act",
        ["32022R2065"] = "DSA",
        ["32022R1925"] = "DMA",
        ["32022R2554"] = "DORA",
        ["32023R1114"] = "MiCA",
        ["32006R1907"] = "REACH",
        ["32017R0745"] = "MDR",
    };

    // Nagłówek jednostki w tekście: „Artykuł 6", „Artykuł 25b".
    private static readonly Regex ArticleHeading = new(@"^Artykuł\s+(\d{1,3}[a-z]{0,2})$", RegexOptions.Compiled);
    // Znacznik artykułu w PŁASKIM tekście (tor legacy i PDF) — nagłówek stoi w osobnej linii.
    private static readonly Regex ArticleMarkerInText =
        new(@"(?:^|\n)\s*Artykuł\s+(\d{1,3}[a-z]{0,2})\s*(?:\n|$)", RegexOptions.Compiled);
    // Oznaczenie aktu z tytułu: „(UE) 2016/679", „(WE) nr 45/2001".
    private static readonly Regex ModernDesignator =
        new(@"\((UE|WE|EWG|Euratom|EWWiS)\)\s*(?:nr\s*)?(\d{1,4}/\d{2,4})", RegexOptions.Compiled);
    // Starszy zapis: „95/46/WE", „2011/83/UE".
    private static readonly Regex LegacyDesignator =
        new(@"\b(\d{2,4}/\d{1,4}/(?:WE|EWG|UE|Euratom|EWWiS))\b", RegexOptions.Compiled);

    /// <summary>
    /// Formuły końcowe, które w korpusie 6 750 aktów dałyby ~12–13 tysięcy niemal identycznych chunków.
    /// To nie estetyka: <c>ChunkDegeneracy</c> powstał w tym projekcie po tym, jak 1 056 chunków
    /// „(pominięty)" i szum anonimizacyjny SAOS wypychały realne przepisy z top-K. Data wejścia w życie
    /// jest metadaną aktu, nie treścią do cytowania.
    /// </summary>
    private static readonly Regex[] Boilerplate =
    [
        new(@"^Niniejsz\w+\s+(rozporządzenie|dyrektywa)\s+wchodzi\s+w\s+życie", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"wiąże\s+w\s+całości\s+i\s+jest\s+bezpośrednio\s+stosowan", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^Niniejsz\w+\s+(rozporządzenie|dyrektywa)\s+stosuje\s+się\s+od\s+dnia", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^Sporządzono\s+w\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    /// <summary>Minimalna liczba słów treści w chunku z WIERSZA TABELI. Załączniki bywają tabelami kodów
    /// (kody CN, wartości MRL) — wiersz „| | (12) | |" nie jest odpowiedzią na żadne pytanie, ale wykaz
    /// systemów wysokiego ryzyka z załącznika III do AI Act już tak. Progiem odsiewamy pierwsze, nie drugie.</summary>
    private const int MinUnitWords = 6;

    public string DocType => DocTypes.EuAct;

    public NormalizedDocument Normalize(RawDocument raw)
    {
        var issues = new List<string>();
        var p = raw.SourcePayload ?? default;
        var celex = StringProp(p, "celex") ?? raw.ExternalId;
        var textCelex = StringProp(p, "textCelex") ?? celex;
        var textVersion = StringProp(p, "textVersion") ?? "base";

        var isPdf = raw.ContentFormat == ContentFormats.PdfText;
        var title = StringProp(p, "title")
            ?? (isPdf ? TitleFromText(raw.RawContent) : TitleFromHtml(raw.RawContent))
            ?? celex;
        var shortTitle = ShortTitle(celex, title);

        var (segments, plainText, path) = Parse(raw, celex, shortTitle, issues);
        DisambiguateDuplicateUnits(segments, issues);

        if (segments.Count == 0)
            issues.Add($"Nie znaleziono jednostek (tor: {path}) — sprawdź strukturę dokumentu; akt bez chunków.");
        if (textVersion == "base")
            issues.Add("Tekst BAZOWY (brak polskiej wersji skonsolidowanej) — treść może nie uwzględniać nowelizacji.");
        if (path == ParsePath.LegacyText)
            issues.Add("Markup legacy (brak kotwic id=\"art_*\") — podział na jednostki z samego tekstu, mniej pewny.");
        if (isPdf)
            issues.Add("Tekst z PDF — struktura jednostek mniej pewna niż w XHTML.");

        var metadata = new Dictionary<string, object?>
        {
            ["celex"] = celex,
            ["textCelex"] = textCelex,
            ["textVersion"] = textVersion,
            ["consolidationDate"] = StringProp(p, "consolidationDate"),
            ["actClass"] = StringProp(p, "actClass"),
            ["euActType"] = ActType(celex),
            ["year"] = CelexYear(celex),
            ["title"] = title,
            ["shortTitle"] = shortTitle,
            ["parsePath"] = path.ToString(),
            ["converterVersion"] = ConverterVersion(raw.RawContent),
            ["amends"] = StringArray(p, "amends"),
            ["repeals"] = StringArray(p, "repeals"),
            // Zakres ingestii bierze tylko akty obowiązujące (filtr SPARQL) — zapisujemy jawnie, żeby
            // filtr „tylko obowiązujące" w wyszukiwaniu obejmował też prawo UE.
            ["inForce"] = true,
            ["displayAddress"] = shortTitle,
        };

        return new NormalizedDocument
        {
            Source = raw.Source,
            ExternalId = raw.ExternalId,
            DocType = DocTypes.Act, // kanonicznie akt — jak NSA zapisuje się jako orzeczenie
            Title = title,
            PlainText = plainText,
            Segments = segments,
            Locator = new CitationLocator { EliId = celex, DisplayAddress = shortTitle, SourceUrl = raw.SourceUrl },
            SourceUrl = raw.SourceUrl,
            SourceModificationDate = raw.SourceModificationDate?.ToUniversalTime(),
            ContentHash = Hashing.Sha256Hex(raw.RawContent),
            TypedMetadata = metadata,
            QualityIssues = issues,
        };
    }

    /// <summary>Tor parsowania — wybierany po zawartości dokumentu, nie po roczniku ani klasie aktu.</summary>
    public enum ParsePath { Anchors, LegacyText, PdfText, None }

    /// <summary>Rozpoznanie toru: kotwice → DOM; brak kotwic, ale jest „Artykuł N" → tekst; nic → pominięcie.</summary>
    public static ParsePath DetectPath(string content, string contentFormat)
    {
        if (string.IsNullOrWhiteSpace(content)) return ParsePath.None;
        if (contentFormat == ContentFormats.PdfText) return ParsePath.PdfText;
        if (content.Contains("id=\"art_", StringComparison.Ordinal)) return ParsePath.Anchors;
        return ArticleMarkerInText.IsMatch(HtmlText.ToPlainText(content)) ? ParsePath.LegacyText : ParsePath.None;
    }

    private (List<DocumentSegment> Segments, string PlainText, ParsePath Path) Parse(
        RawDocument raw, string celex, string shortTitle, List<string> issues)
    {
        var path = DetectPath(raw.RawContent, raw.ContentFormat);
        var segments = new List<DocumentSegment>();
        var full = new StringBuilder();

        switch (path)
        {
            case ParsePath.Anchors:
                ParseByAnchors(raw.RawContent, celex, shortTitle, raw.SourceUrl, segments, full);
                break;
            case ParsePath.LegacyText:
            case ParsePath.PdfText:
                var text = raw.ContentFormat == ContentFormats.PdfText
                    ? raw.RawContent
                    : HtmlText.ToPlainText(StripNoise(raw.RawContent));
                ParseByTextMarkers(text, celex, shortTitle, raw.SourceUrl, segments, full);
                break;
            default:
                issues.Add("Dokument nie ma ani kotwic id=\"art_*\", ani znaczników „Artykuł N\" — możliwa zmiana schematu CELLAR-a.");
                break;
        }

        return (segments, full.ToString().TrimEnd(), path);
    }

    // ---------- tor 1: kotwice w DOM ----------

    private void ParseByAnchors(
        string html, string celex, string shortTitle, string? sourceUrl,
        List<DocumentSegment> segments, StringBuilder full)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        RemoveNoiseNodes(doc.DocumentNode);

        foreach (var node in doc.DocumentNode.SelectNodes("//div[@id]") ?? Enumerable.Empty<HtmlNode>())
        {
            var id = node.GetAttributeValue("id", "");
            var (unitLabel, isArticle) = UnitFromId(id);
            if (unitLabel is null) continue; // „art_6.tit_1" i inne kontenery pomijamy

            var chapter = isArticle ? ChapterTitle(node) : null;
            var (subtitle, body) = SplitHeadingAndBody(node, unitLabel, isArticle);
            if (string.IsNullOrWhiteSpace(body)) continue;

            EmitUnits(segments, full, body, unitLabel, subtitle, chapter, shortTitle, celex, sourceUrl);
        }
    }

    /// <summary>„art_6" → („art. 6", artykuł); „anx_III" → („załącznik III", nie-artykuł); inne id → null.
    /// Załączniki MUSZĄ mieć własny lokalizator: treść wykazu systemów wysokiego ryzyka z AI Act siedzi
    /// w <c>anx_III</c>, poza kontenerami artykułów — parser oparty tylko na artykułach milcząco ją gubi.</summary>
    public static (string? Label, bool IsArticle) UnitFromId(string id)
    {
        if (id.StartsWith("art_", StringComparison.Ordinal))
        {
            var n = id["art_".Length..];
            var ok = n.Length > 0 && char.IsAsciiDigit(n[0])
                && n.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c));
            return ok ? ($"art. {n}", true) : (null, false);
        }

        if (id.StartsWith("anx_", StringComparison.Ordinal))
        {
            var n = id["anx_".Length..];
            var ok = n.Length > 0 && n.All(c => char.IsAsciiLetterOrDigit(c));
            return ok ? ($"załącznik {n}", false) : (null, false);
        }

        return (null, false);
    }

    /// <summary>Usuwa węzły, które nie są treścią normy. Krytyczne dla poprawności, nie kosmetyczne:
    /// <c>p.modref</c> to znaczniki wersji („▼M1", „▼B") stojące W ŚRODKU zdania w tekstach skonsolidowanych,
    /// a <c>p.disclaimer</c> to zdanie „nie ma mocy prawnej" wklejone do aktu, który cytujemy jako prawo.</summary>
    private static void RemoveNoiseNodes(HtmlNode root)
    {
        const string classes = "modref oj-note reference disclaimer arrow oj-hd-date oj-hd-lg oj-hd-ti oj-hd-oj oj-signatory signatory";
        foreach (var cls in classes.Split(' '))
        {
            var nodes = root.SelectNodes($"//*[contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')]");
            foreach (var n in nodes?.ToArray() ?? []) n.Remove();
        }
        foreach (var n in root.SelectNodes("//comment()|//script|//style")?.ToArray() ?? []) n.Remove();
    }

    /// <summary>To samo odsianie, ale dla toru tekstowego (najpierw czyścimy DOM, potem spłaszczamy).</summary>
    private static string StripNoise(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        RemoveNoiseNodes(doc.DocumentNode);
        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>Zdejmuje nagłówek jednostki i jej tytuł (<c>div.eli-title</c>), zwracając tytuł osobno
    /// (idzie do nagłówka kontekstowego chunka) oraz spłaszczoną treść.</summary>
    private static (string? Subtitle, string Body) SplitHeadingAndBody(HtmlNode unit, string unitLabel, bool isArticle)
    {
        var clone = unit.CloneNode(true);

        string? subtitle = null;
        foreach (var t in clone.SelectNodes(".//div[contains(concat(' ', normalize-space(@class), ' '), ' eli-title ')]")?.ToArray() ?? [])
        {
            subtitle ??= Collapse(HtmlEntity.DeEntitize(t.InnerText));
            t.Remove();
        }

        var body = HtmlText.ToPlainText(clone.OuterHtml);
        var lines = body.Split('\n').ToList();

        // Pierwsza linia to nagłówek („Artykuł 6" / „ZAŁĄCZNIK III") — numer żyje w lokalizatorze, nie w treści.
        if (lines.Count > 0)
        {
            var first = lines[0].Trim();
            var isHeading = isArticle
                ? ArticleHeading.IsMatch(first)
                : first.StartsWith("ZAŁĄCZNIK", StringComparison.OrdinalIgnoreCase) && first.Length <= 30;
            if (isHeading) lines.RemoveAt(0);
        }

        // Tytuł załącznika bywa zwykłym akapitem, nie div.eli-title — bierzemy pierwszą krótką linię.
        if (!isArticle && subtitle is null && lines.Count > 0 && lines[0].Trim().Length is > 0 and <= 120)
        {
            subtitle = lines[0].Trim();
            lines.RemoveAt(0);
        }

        _ = unitLabel;
        return (string.IsNullOrWhiteSpace(subtitle) ? null : subtitle, string.Join("\n", lines).Trim());
    }

    /// <summary>Rozdział z najbliższego przodka <c>div[id^='cpt_']</c> — „ROZDZIAŁ II – Zasady".</summary>
    private static string? ChapterTitle(HtmlNode article)
    {
        var chapter = article.Ancestors("div")
            .FirstOrDefault(a => a.GetAttributeValue("id", "").StartsWith("cpt_", StringComparison.Ordinal));
        if (chapter is null) return null;

        var titles = new List<string>();
        foreach (var cls in new[] { "oj-ti-section-1", "title-division-1", "oj-ti-section-2", "title-division-2" })
        {
            var node = chapter.SelectSingleNode($"./p[contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')]");
            if (node is null) continue;
            var text = Collapse(HtmlEntity.DeEntitize(node.InnerText));
            if (text.Length > 0 && !titles.Contains(text)) titles.Add(text);
        }
        return titles.Count == 0 ? null : string.Join(" – ", titles);
    }

    // ---------- tor 2/3: znaczniki w płaskim tekście (legacy XHTML i PDF) ----------

    private void ParseByTextMarkers(
        string text, string celex, string shortTitle, string? sourceUrl,
        List<DocumentSegment> segments, StringBuilder full)
    {
        var clean = text.Replace("\r", "");
        var markers = ArticleMarkerInText.Matches(clean);
        for (var i = 0; i < markers.Count; i++)
        {
            var number = markers[i].Groups[1].Value;
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : clean.Length;
            var body = clean[start..end].Trim();
            if (body.Length == 0) continue;

            EmitUnits(segments, full, body, $"art. {number}", subtitle: null, chapter: null, shortTitle, celex, sourceUrl);
        }
    }

    // ---------- wspólne ----------

    private void EmitUnits(
        List<DocumentSegment> segments, StringBuilder full, string body,
        string unitLabel, string? subtitle, string? chapter, string shortTitle, string celex, string? sourceUrl)
    {
        var article = unitLabel.StartsWith("art. ", StringComparison.Ordinal) ? unitLabel["art. ".Length..] : null;
        var annex = article is null ? unitLabel : null;

        foreach (var u in EuActUnitSplitter.Split(body))
        {
            if (IsBoilerplate(u.Text) || !HasEnoughContent(u.Text)) continue;

            var label = EuActUnitSplitter.Label(unitLabel, u.Paragraph, u.Point, u.Kind);
            var header = string.Join(", ", new[]
            {
                shortTitle,
                chapter,
                subtitle is null ? label : $"{label} ({subtitle})",
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var text = header.Length > 0 ? header + "\n" + u.Text : u.Text;
            var start = full.Length;
            full.Append(text).Append("\n\n");

            segments.Add(new DocumentSegment
            {
                Text = text,
                Kind = "article",
                Label = label,
                ContextHeader = header,
                CharStart = start,
                Locator = new CitationLocator
                {
                    EliId = celex,
                    // Numer artykułu trafia do denormalizowanej kolumny ArticleNo (tor strukturalny QU-1);
                    // dla załącznika numeru artykułu NIE ma — jednostkę niesie etykieta i kotwica.
                    Article = article,
                    Paragraph = u.Paragraph,
                    Point = u.Point,
                    DisplayAddress = shortTitle,
                    Anchor = annex is null ? $"art_{article}" : annex.Replace("załącznik ", "anx_"),
                    SourceUrl = sourceUrl,
                },
            });
        }
    }

    /// <summary>Formuła końcowa („wchodzi w życie…", „wiąże w całości…") nie jest chunkiem — patrz
    /// <see cref="Boilerplate"/>. W korpusie 6 750 aktów to ~12–13 tys. niemal identycznych wektorów.</summary>
    public static bool IsBoilerplate(string text)
    {
        var normalized = Collapse(text);
        return Boilerplate.Any(re => re.IsMatch(normalized));
    }

    /// <summary>Próg treściowy na jednostkę — odsiewa wiersze tabel kodowych z załączników, zostawia
    /// wykazy pisane zdaniami (np. załącznik III do AI Act).</summary>
    private static bool HasEnoughContent(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length >= 3) >= MinUnitWords;

    /// <summary>
    /// Gwarantuje unikalność lokalizatora w dokumencie (precedens <c>DisambiguateDuplicateUnits</c>
    /// z ActNormalizer): tekst skonsolidowany potrafi zawierać jednostkę o tym samym numerze w dwóch
    /// brzmieniach. Nie usuwamy — oznaczamy wariantami i zgłaszamy do przeglądu.
    /// </summary>
    private static void DisambiguateDuplicateUnits(List<DocumentSegment> segments, List<string> issues)
    {
        var dupes = segments
            .Select((s, i) => (s, i))
            .GroupBy(x => x.s.Label)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in dupes)
        {
            var members = g.ToList();
            issues.Add($"Duplikat jednostki „{members[0].s.Label}” — {members.Count} wersje; oznaczono wariantami.");
            for (var k = 0; k < members.Count; k++)
            {
                var (seg, idx) = members[k];
                var suffix = $" (wariant {k + 1}/{members.Count})";
                var newHeader = (seg.ContextHeader ?? "") + suffix;
                var nl = seg.Text.IndexOf('\n');
                var newText = nl >= 0 ? newHeader + seg.Text[nl..] : newHeader + "\n" + seg.Text;
                segments[idx] = seg with { Label = (seg.Label ?? "") + suffix, ContextHeader = newHeader, Text = newText };
            }
        }
    }

    private static string? TitleFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var cls in new[] { "oj-doc-ti", "title-doc-first" })
        {
            var nodes = doc.DocumentNode.SelectNodes(
                $"//p[contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')]");
            if (nodes is null || nodes.Count == 0) continue;
            var parts = nodes.Select(n => Collapse(HtmlEntity.DeEntitize(n.InnerText)))
                .Where(s => s.Length > 0).ToList();
            if (parts.Count > 0) return string.Join(" ", parts);
        }
        return null;
    }

    private static string? TitleFromText(string text)
    {
        foreach (var line in text.Replace("\r", "").Split('\n').Take(60))
        {
            var l = Collapse(line);
            if (l.Length < 12) continue;
            if (l.StartsWith("ROZPORZĄDZENIE", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("DYREKTYWA", StringComparison.OrdinalIgnoreCase))
                return l;
        }
        return null;
    }

    /// <summary>Krótka nazwa do nagłówka chunka: nazwa zwyczajowa („RODO") → oznaczenie z tytułu
    /// („rozporządzenie (UE) 2016/679") → CELEX.</summary>
    private static string ShortTitle(string celex, string title)
    {
        if (ShortNames.TryGetValue(celex, out var known)) return known;

        var normalized = EuActClassifier.NormalizeWhitespace(title);
        var kind = ActType(celex) ?? "akt";
        if (ModernDesignator.Match(normalized) is { Success: true } m)
            return $"{kind} ({m.Groups[1].Value}) {m.Groups[2].Value}";
        if (LegacyDesignator.Match(normalized) is { Success: true } l)
            return $"{kind} {l.Groups[1].Value}";
        return celex;
    }

    /// <summary>Wersja konwertera z komentarza HTML — diagnostyka przy zmianie schematu CELLAR-a
    /// (widziane 5.4, 6.7, 7.6.2, 8.4.1, 9.6–9.18; brak kotwic dotyczy wersji starszych niż 9.x).</summary>
    private static string? ConverterVersion(string content)
    {
        var m = Regex.Match(content, @"converter_version:([0-9.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Typ aktu z CELEX-u (sektor 3, litera po roku).</summary>
    private static string? ActType(string celex) =>
        celex.Length >= 6
            ? celex[5] switch { 'R' => "rozporządzenie", 'L' => "dyrektywa", 'D' => "decyzja", _ => null }
            : null;

    private static int? CelexYear(string celex) =>
        celex.Length >= 5 && int.TryParse(celex.AsSpan(1, 4), out var y) ? y : null;

    private static string Collapse(string s) =>
        EuActClassifier.NormalizeWhitespace(s.Replace("\n", " ").Replace("\r", " "));

    private static string? StringProp(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string[] StringArray(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
