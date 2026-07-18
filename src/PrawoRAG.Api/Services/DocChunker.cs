using System.Text;

namespace PrawoRAG.Api.Services;

/// <summary>
/// Chunker załączników (DOC-1) — celowo NIE reużywa TokenAwareChunker z ingestii: Ingestion to
/// projekt exe (konektory SAOS/ELI), a dokładne liczenie tokenów przez TEI /tokenize jest tu
/// zbędne — TEI ma <c>--auto-truncate</c>, więc konserwatywny limit ZNAKÓW (~1400 zn ≈ 400 tok
/// polskiego tekstu, głęboko pod limitem mmlw) degraduje bezpiecznie. Czysty i deterministyczny.
/// </summary>
public static class DocChunker
{
    /// <summary>Docelowy rozmiar chunka w znakach (~400 tokenów polskiego tekstu).</summary>
    public const int MaxChunkChars = 1400;

    /// <summary>Akapity (linie) pakowane zachłannie do limitu; akapit dłuższy niż limit dzielony
    /// na granicy słowa. Puste strony/linie odpadają.</summary>
    public static IReadOnlyList<string> Split(IReadOnlyList<string> pages, int maxChars = MaxChunkChars)
    {
        var units = pages
            .SelectMany(p => p.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .SelectMany(l => SplitOversize(l, maxChars))
            .ToList();

        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var unit in units)
        {
            if (sb.Length > 0 && sb.Length + 1 + unit.Length > maxChars)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(unit);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }

    /// <summary>Akapit > limit → kawałki ≤ limit, cięte na ostatniej spacji (bez spacji — na surowo).</summary>
    private static IEnumerable<string> SplitOversize(string text, int maxChars)
    {
        while (text.Length > maxChars)
        {
            var cut = text.LastIndexOf(' ', maxChars - 1);
            if (cut <= 0) cut = maxChars;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
