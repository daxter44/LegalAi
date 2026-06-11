using System.Text;
using HtmlAgilityPack;

namespace PrawoRAG.Ingestion.Saos;

/// <summary>Konwersja HTML orzeczenia SAOS na czysty tekst (akapity jako podwójny newline, encje rozkodowane,
/// komentarze usunięte, treść `anon-block` zachowana). </summary>
internal static class HtmlText
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article" };

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Usuń komentarze (np. <!-- -->) i skrypty/style.
        foreach (var n in doc.DocumentNode.SelectNodes("//comment()|//script|//style")?.ToArray() ?? [])
            n.Remove();

        var sb = new StringBuilder();
        foreach (var node in doc.DocumentNode.Descendants())
        {
            if (node.NodeType == HtmlNodeType.Element && BlockTags.Contains(node.Name))
                sb.Append('\n');
            else if (node.NodeType == HtmlNodeType.Text)
                sb.Append(HtmlEntity.DeEntitize(node.InnerText));
        }
        return Normalize(sb.ToString());
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToList();

        var sb = new StringBuilder();
        var blankRun = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (++blankRun <= 1 && sb.Length > 0) sb.Append('\n');
            }
            else
            {
                blankRun = 0;
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString().Trim();
    }
}
