using Microsoft.Extensions.Options;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Embeddings;

namespace PrawoRAG.Ingestion.Chunking;

/// <summary>
/// Dzieli segmenty na chunki ≤ limitu tokenów modelu, z zakładką. Liczbę tokenów liczy dokładnie
/// przez <see cref="IEmbeddingProvider.CountTokensAsync"/> (TEI /tokenize). Jednostki = akapity (linie);
/// akapit przekraczający limit jest dzielony tokenowo (połowienie + ponowne liczenie), więc twardy
/// limit jest gwarantowany niezależnie od długości tokenów.
/// </summary>
public sealed class TokenAwareChunker(IEmbeddingProvider embedder, IOptions<ChunkerOptions> options) : IChunker
{
    private readonly ChunkerOptions _opt = options.Value;

    public async Task<IReadOnlyList<DocumentChunk>> ChunkAsync(NormalizedDocument document, CancellationToken ct)
    {
        var chunks = new List<DocumentChunk>();
        var index = 0;

        foreach (var segment in document.Segments)
        {
            var units = SplitUnits(segment.Text);
            if (units.Count == 0) continue;

            var counts = await CountAsync(units, ct);
            (units, counts) = await EnsureWithinMaxAsync(units, counts, ct);

            foreach (var packed in Pack(units, counts))
            {
                // Odrzuć zdegenerowane fragmenty (checkboxy formularza, „⚫", pojedyncze linie, urwane słowa) —
                // mają anomalnie wysokie cosine do KAŻDEGO zapytania i wypychają realne przepisy z top-K.
                if (CountSubstantiveWords(packed.Text) < _opt.MinSubstantiveWords) continue;

                chunks.Add(new DocumentChunk
                {
                    ChunkIndex = index++,
                    Text = packed.Text,
                    Section = segment.Label,
                    CharStart = segment.CharStart + packed.LocalStart,
                    CharEnd = segment.CharStart + packed.LocalStart + packed.Text.Length,
                    TokenCount = packed.Tokens,
                    Locator = segment.Locator,
                });
            }
        }
        return chunks;
    }

    private readonly record struct Unit(string Text, int Start);
    private readonly record struct Packed(string Text, int LocalStart, int Tokens);

    private static readonly System.Text.RegularExpressions.Regex SubstantiveWord =
        new(@"[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{3,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Liczba „sensownych słów" = ciągów ≥3 liter. Miara realnej treści chunka
    /// (odsiew scaffoldingu formularzy SAOS i artefaktów HTML→tekst).</summary>
    private static int CountSubstantiveWords(string text) => SubstantiveWord.Matches(text).Count;

    /// <summary>Akapity (linie) jako jednostki, z offsetem w tekście segmentu.</summary>
    private static List<Unit> SplitUnits(string text)
    {
        var units = new List<Unit>();
        var pos = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                var start = text.IndexOf(trimmed, Math.Min(pos, text.Length), StringComparison.Ordinal);
                units.Add(new Unit(trimmed, start < 0 ? pos : start));
            }
            pos += line.Length + 1; // +1 za '\n'
        }
        return units;
    }

    private async Task<int[]> CountAsync(List<Unit> units, CancellationToken ct)
    {
        var counts = await embedder.CountTokensAsync(units.Select(u => u.Text).ToList(), ct);
        return counts.ToArray();
    }

    /// <summary>Dzieli jednostki dłuższe niż MaxTokens na pół (na granicy spacji) i przelicza, aż wszystkie się zmieszczą.</summary>
    private async Task<(List<Unit>, int[])> EnsureWithinMaxAsync(List<Unit> units, int[] counts, CancellationToken ct)
    {
        for (var guard = 0; guard < 12; guard++)
        {
            if (!counts.Where((c, i) => c > _opt.MaxTokens).Any()) break;

            var next = new List<Unit>(units.Count + 4);
            for (var i = 0; i < units.Count; i++)
            {
                if (counts[i] <= _opt.MaxTokens) { next.Add(units[i]); continue; }
                var (left, right) = SplitInHalf(units[i]);
                next.Add(left);
                if (right.Text.Length > 0) next.Add(right);
            }
            units = next;
            counts = await CountAsync(units, ct);
        }
        return (units, counts);
    }

    private static (Unit Left, Unit Right) SplitInHalf(Unit u)
    {
        var mid = u.Text.Length / 2;
        var sp = u.Text.LastIndexOf(' ', Math.Min(mid, u.Text.Length - 1));
        if (sp <= 0) sp = mid; // brak spacji — tnij na surowo
        var left = u.Text[..sp];
        var right = u.Text[sp..].TrimStart();
        var rightOffset = sp + (u.Text[sp..].Length - right.Length);
        return (new Unit(left.TrimEnd(), u.Start), new Unit(right, u.Start + rightOffset));
    }

    /// <summary>Greedy: pakuj jednostki do TargetTokens, następny chunk z zakładką OverlapTokens.</summary>
    private IEnumerable<Packed> Pack(List<Unit> units, int[] counts)
    {
        var i = 0;
        while (i < units.Count)
        {
            var start = i;
            var tokens = 0;
            while (i < units.Count && (i == start || tokens + counts[i] <= _opt.TargetTokens))
            {
                tokens += counts[i];
                i++;
            }

            var localStart = units[start].Start;
            var text = string.Join("\n", Enumerable.Range(start, i - start).Select(k => units[k].Text));
            yield return new Packed(text, localStart, tokens);

            if (i >= units.Count) yield break;

            var overlap = 0;
            var back = i;
            for (var j = i - 1; j > start && overlap + counts[j] <= _opt.OverlapTokens; j--)
            {
                overlap += counts[j];
                back = j;
            }
            i = back > start ? back : i; // gwarancja postępu
        }
    }
}
