using PrawoRAG.Domain.Embeddings;

namespace PrawoRAG.Api.Services;

/// <summary>Fragment załącznika wybrany do promptu: <see cref="Index"/> to numer w przestrzeni
/// cytowań dokumentu TEJ tury ([D1], [D2]…) — przydzielany per pytanie, nie per plik.</summary>
public sealed record DocFragment(int Index, string Text);

/// <summary>
/// Załącznik użytkownika po przetworzeniu (DOC-1): chunki + embeddingi trzymane WYŁĄCZNIE w pamięci
/// obwodu Blazora (decyzja #1 — pisma klientów nie dotykają bazy ani dysku; odświeżenie strony =
/// dokument trzeba dołączyć ponownie). Doc-retrieval = cosine w pamięci: ~100 chunków to mikrosekundy,
/// pgvector byłby infrastrukturą bez potrzeby (decyzja #2).
/// </summary>
public sealed class DocumentContext
{
    /// <summary>Ile fragmentów dokumentu wchodzi do promptu obok źródeł korpusu (budżet promptu
    /// przy TopK=8 źródeł; do rewizji po pomiarach na M4).</summary>
    public const int DefaultTopK = 4;

    public required string FileName { get; init; }
    public required int PageCount { get; init; }
    public required bool Truncated { get; init; }
    public required IReadOnlyList<string> Chunks { get; init; }
    public required IReadOnlyList<float[]> Embeddings { get; init; }

    public static async Task<DocumentContext> CreateAsync(
        string fileName, PdfText pdf, IEmbeddingProvider embedder, CancellationToken ct)
    {
        var chunks = DocChunker.Split(pdf.Pages);
        var embeddings = await embedder.EmbedPassagesAsync(chunks, ct);
        return new DocumentContext
        {
            FileName = fileName, PageCount = pdf.PageCount, Truncated = pdf.Truncated,
            Chunks = chunks, Embeddings = embeddings,
        };
    }

    /// <summary>Top-K chunków po cosine do pytania, zwrócone w KOLEJNOŚCI DOKUMENTU (fragmenty
    /// czytane po kolei są zrozumiałe dla modelu i człowieka; ranking służy tylko selekcji)
    /// i przenumerowane [D1..K] na potrzeby tej tury.</summary>
    public IReadOnlyList<DocFragment> SelectFragments(float[] queryEmbedding, int topK = DefaultTopK)
    {
        if (Chunks.Count == 0 || topK <= 0) return [];
        return Embeddings
            .Select((e, i) => (Position: i, Sim: Cosine(queryEmbedding, e)))
            .OrderByDescending(x => x.Sim)
            .Take(topK)
            .OrderBy(x => x.Position)
            .Select((x, k) => new DocFragment(k + 1, Chunks[x.Position]))
            .ToList();
    }

    /// <summary>Cosine z jawną normalizacją — TEI zwraca wektory znormalizowane, ale fake'i testowe
    /// i przyszłe providery nie muszą; koszt pomijalny przy ~100 chunkach.</summary>
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new InvalidOperationException(
                $"Niezgodność wymiarów embeddingów ({a.Length} vs {b.Length}) — zapytanie i dokument muszą iść przez ten sam model.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
