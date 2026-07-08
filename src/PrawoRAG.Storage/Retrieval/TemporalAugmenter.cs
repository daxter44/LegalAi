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
/// Nowela jest mała (kilka chunków) → wczytanie i filtr w pamięci są tanie.
///
/// AKT-4b: dodatkowo OZNACZA (nie dokłada) każdy chunk JUŻ obecny w wynikach, którego WŁASNY dokument jest
/// niewchłoniętą nowelą — nawet gdy trafił tam zwykłą ścieżką semantyczną (pytanie opisowe, nie cytat
/// artykułu), nie przez dopasowanie cytatu wyżej. Zmierzone na M4: pytanie sparafrazowane blisko treści
/// noweli trafia NA SAMĄ NOWELĘ jako zwykły, nieoznaczony wynik — dla użytkownika to bez różnicy JAK
/// nowela trafiła do źródeł, oznaczenie ma się pojawić zawsze. Wynik: WHOLE lista (oznaczone + dołożone),
/// nie tylko dołożenia — caller podmienia całą listę wynikiem, nie dokleja go do starej.
/// </summary>
public sealed class TemporalAugmenter(PrawoRagDbContext db) : ITemporalAugmenter
{
    public async Task<IReadOnlyList<RetrievedChunk>> AugmentAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> retrieved, CancellationToken ct)
    {
        var actDocIds = retrieved.Where(c => c.DocType == DocTypes.Act).Select(c => c.DocumentId).Distinct().ToList();
        if (actDocIds.Count == 0) return retrieved;

        // AKT-4b: globalny słownik ExternalId→EffectiveDate dla WSZYSTKICH niewchłoniętych nowel w korpusie
        // (nie tylko tych, których akt bazowy jest akurat w retrieved) — do oznaczenia źródeł-nowel, które
        // trafiły do wyników zwykłym retrievalem. Koszt: skan metadanych aktów (tanie przy dzisiejszej
        // skali korpusu ~40 aktów; przy „pełnym korpusie" v1 wymagałoby indeksu/cache — poza zakresem teraz).
        var unabsorbedDates = await BuildUnabsorbedDatesAsync(ct);
        var extIdByDocId = await db.Documents.Where(d => actDocIds.Contains(d.Id))
            .Select(d => new { d.Id, d.ExternalId }).ToDictionaryAsync(x => x.Id, x => x.ExternalId, ct);

        var tagged = retrieved.Select(c =>
            c.AmendmentEffectiveDate is null && extIdByDocId.TryGetValue(c.DocumentId, out var extId)
                && unabsorbedDates.TryGetValue(extId, out var date)
                ? c with { AmendmentEffectiveDate = date }
                : c).ToList();

        // Artykuły w zainteresowaniu: z lokatorów zwróconych chunków aktu + z cytatów w pytaniu.
        var articlesByDoc = retrieved
            .Where(c => c.DocType == DocTypes.Act && c.Locator?.Article is not null)
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Locator!.Article!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var citedArticles = CitationParser.Parse(query.Text).Select(x => x.Article).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var docs = await db.Documents.Where(d => actDocIds.Contains(d.Id)).ToListAsync(ct);
        var seen = new HashSet<Guid>(tagged.Select(c => c.ChunkId));

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
                    tagged.Add(ToAmendmentChunk(ch, am));
                }
            }
        }
        return tagged;
    }

    private async Task<Dictionary<string, string?>> BuildUnabsorbedDatesAsync(CancellationToken ct)
    {
        var metas = await db.Documents.Where(d => d.DocType == DocTypes.Act)
            .Select(d => d.TypedMetadata).ToListAsync(ct);
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
            foreach (var am in ParseUnabsorbed(meta))
                map[am.EliId] = am.EffectiveDate;
        return map;
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
            AmendmentEffectiveDate = am.EffectiveDate, // AKT-4: pod chip w UI, niezależnie od markera w Text
        };
    }
}
