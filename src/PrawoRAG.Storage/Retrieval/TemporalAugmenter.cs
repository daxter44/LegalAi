using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Storage.Retrieval;

/// <summary>
/// AKT-2: gdy retrieval zwrócił akt, dla którego istnieją nowele NIEWCHŁONIĘTE do tekstu jednolitego
/// (metadane AKT-0/1), dokłada fragmenty tych nowel dotyczące pytanych artykułów. Nowela ma własny numer
/// artykułu (nie linkuje się przez ArticleNo aktu), więc dopasowujemy po TREŚCI diffu („…w art. 94 § 2…").
/// Nowela jest mała (kilka chunków) → wczytanie i filtr w pamięci są tanie. DOKŁADA, nigdy nie usuwa.
/// </summary>
public sealed class TemporalAugmenter(PrawoRagDbContext db) : ITemporalAugmenter
{
    public async Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
    {
        var actDocIds = retrieved.Where(c => c.DocType == DocTypes.Act).Select(c => c.DocumentId).Distinct().ToList();
        if (actDocIds.Count == 0) return [];

        // Artykuły w zainteresowaniu: z lokatorów zwróconych chunków aktu + z cytatów w pytaniu.
        var articlesByDoc = retrieved
            .Where(c => c.DocType == DocTypes.Act && c.Locator?.Article is not null)
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Locator!.Article!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var citedArticles = CitationParser.Parse(query.Text).Select(x => x.Article).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var docs = await db.Documents.Where(d => actDocIds.Contains(d.Id)).ToListAsync(ct);
        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();

        foreach (var d in docs)
        {
            var amendments = ParseUnabsorbed(d.TypedMetadata);
            if (amendments.Count == 0) continue;

            var articles = new HashSet<string>(citedArticles, StringComparer.OrdinalIgnoreCase);
            if (articlesByDoc.TryGetValue(d.Id, out var arts)) articles.UnionWith(arts);
            if (articles.Count == 0) continue;

            foreach (var am in amendments)
            {
                var amChunks = await db.Chunks.Include(c => c.Document)
                    .Where(c => c.Document!.ExternalId == am.EliId).ToListAsync(ct);
                foreach (var ch in amChunks)
                {
                    if (!articles.Any(a => MentionsArticle(ch.Text, a))) continue;
                    if (!seen.Add(ch.Id)) continue;
                    result.Add(ToAmendmentChunk(ch, am));
                }
            }
        }
        return result;
    }

    private static List<AmendmentRef> ParseUnabsorbed(JsonDocument? meta)
    {
        if (meta is null
            || !meta.RootElement.TryGetProperty("unabsorbedAmendments", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];
        try { return arr.Deserialize<List<AmendmentRef>>() ?? []; }
        catch { return []; }
    }

    private static bool MentionsArticle(string text, string article) =>
        Regex.IsMatch(text, @"\bart\.?\s*" + Regex.Escape(article) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static RetrievedChunk ToAmendmentChunk(ChunkEntity ch, AmendmentRef am)
    {
        var date = string.IsNullOrWhiteSpace(am.EffectiveDate) ? "" : $" obowiązuje od {am.EffectiveDate},";
        var marker = $"[NOWELIZACJA —{date} jeszcze niewchłonięta do tekstu jednolitego]\n";
        return new RetrievedChunk
        {
            ChunkId = ch.Id,
            DocumentId = ch.DocumentId,
            Text = marker + ch.Text,
            Section = ch.Section,
            Source = ch.Document!.Source,
            DocType = ch.Document.DocType,
            Title = ch.Document.Title,
            SourceUrl = ch.Document.SourceUrl,
            Locator = ch.Locator is null ? null : ch.Locator.Deserialize<CitationLocator>(),
            Score = double.MaxValue, // świeża nowela — prominentnie
            Similarity = null,
        };
    }
}
