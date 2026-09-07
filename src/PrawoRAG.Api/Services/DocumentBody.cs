namespace PrawoRAG.Api.Services;

/// <summary>Sekcja dokumentu do wyświetlenia (np. „Sentencja", „Uzasadnienie", artykuł aktu).</summary>
public sealed record DocumentSection(string? Label, string Text);

/// <summary>
/// Składa treść dokumentu z jego CHUNKÓW (jedyne, co mamy — pełnego tekstu nie przechowujemy).
/// Czysta, testowalna: chunki w kolejności → scalone sekcje. Chunki tej samej sekcji nakładają się
/// (zakładka chunkera, <c>OverlapTokens</c>) — łączymy je z PRZYCINANIEM POKRYWAJĄCYCH SIĘ LINII,
/// żeby granice nie dublowały zdań. Chunki różnych sekcji nie nakładają się (osobne segmenty), więc
/// przycinanie ich nie tyka. Etykiety sekcji orzeczeń tłumaczone na czytelne nagłówki.
/// </summary>
public static class DocumentBody
{
    public static IReadOnlyList<DocumentSection> Assemble(IReadOnlyList<(string? Section, string Text)> chunks)
    {
        var sections = new List<DocumentSection>();
        var i = 0;
        while (i < chunks.Count)
        {
            var label = chunks[i].Section;
            var start = i;
            var acc = "";
            while (i < chunks.Count && chunks[i].Section == label)
            {
                acc = acc.Length == 0 ? chunks[i].Text.Trim() : JoinTrimmingOverlap(acc, chunks[i].Text.Trim());
                i++;
            }
            if (acc.Length > 0) sections.Add(new DocumentSection(FriendlyLabel(label), acc));
            _ = start;
        }
        return sections;
    }

    /// <summary>Łączy dwa fragmenty, usuwając z początku drugiego linie identyczne z końcem pierwszego
    /// (zakładka chunkera to całe powtórzone linie). Bez pokrycia → zwykłe sklejenie z nową linią.</summary>
    private static string JoinTrimmingOverlap(string prev, string next)
    {
        var prevLines = prev.Split('\n');
        var nextLines = next.Split('\n');
        var maxOverlap = Math.Min(prevLines.Length, nextLines.Length);
        for (var k = maxOverlap; k > 0; k--)
        {
            var prevTail = prevLines[^k..];
            var nextHead = nextLines[..k];
            if (prevTail.SequenceEqual(nextHead, StringComparer.Ordinal))
                return prev + "\n" + string.Join("\n", nextLines[k..]);
        }
        return prev + "\n" + next;
    }

    private static string? FriendlyLabel(string? section) => section switch
    {
        "komparycja" => "Komparycja",
        "sentencja" => "Sentencja",
        "uzasadnienie" => "Uzasadnienie",
        "document" or null or "" => null, // brak sensownego nagłówka → bez etykiety
        _ => section, // artykuły aktów itp. — pokaż jak jest
    };
}
