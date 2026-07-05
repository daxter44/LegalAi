using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using PrawoRAG.Domain.Documents;
using PrawoRAG.Domain.Embeddings;
using PrawoRAG.Domain.Retrieval;
using PrawoRAG.Storage.Entities;

namespace PrawoRAG.Storage.Retrieval;

/// <summary>
/// Wyszukiwanie hybrydowe: tor gęsty (pgvector cosine) + tor rzadki (tsvector BM25), fuzja RRF.
/// Numery artykułów/sygnatury łapie BM25; intencję semantyczną — dense. Filtry metadanych w SQL.
/// </summary>
public sealed class HybridRetriever(PrawoRagDbContext db, IEmbeddingProvider embedder, IReranker? reranker = null) : IRetriever
{
    private const int RrfK = 60;

    /// <summary>
    /// hnsw.ef_search dla toru gęstego. Domyślne 40 daje słaby recall przy filtrze i gęstwinie
    /// bliskich konkurentów — indeks (aproksymacyjny) potrafi POMINĄĆ prawdziwie najbliższy wektor
    /// (np. właściwy artykuł kodeksu). 400 przywraca poprawny ranking kosztem nieco wolniejszego skanu.
    /// </summary>
    private const int HnswEfSearch = 400;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        var qvec = new Vector(await embedder.EmbedQueryAsync(query.Text, ct));
        var k = query.CandidatesPerPath;

        var filtered = ApplyFilters(db.Chunks.Where(c => c.Embedding != null), query);

        // ef_search musi obowiązywać na TYM samym połączeniu co zapytanie dense — transakcja to gwarantuje.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL hnsw.ef_search = {HnswEfSearch}", ct);

        // Tor gęsty: najmniejszy dystans cosine.
        var dense = await filtered
            .Select(c => new { c.Id, Dist = c.Embedding!.CosineDistance(qvec) })
            .OrderBy(x => x.Dist)
            .Take(k)
            .ToListAsync(ct);

        // Tor rzadki: BM25 po tsvector (konfiguracja zgodna z kolumną generowaną).
        var sparse = await ApplyFilters(db.Chunks, query)
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, query.Text)))
            .Select(c => new { c.Id, Rank = c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, query.Text)) })
            .OrderByDescending(x => x.Rank)
            .Take(k)
            .ToListAsync(ct);

        // Fuzja RRF.
        var rrf = new Dictionary<Guid, double>();
        var sim = new Dictionary<Guid, double>();
        for (var i = 0; i < dense.Count; i++)
        {
            rrf[dense[i].Id] = rrf.GetValueOrDefault(dense[i].Id) + 1.0 / (RrfK + i + 1);
            sim[dense[i].Id] = 1.0 - dense[i].Dist;
        }
        for (var i = 0; i < sparse.Count; i++)
            rrf[sparse[i].Id] = rrf.GetValueOrDefault(sparse[i].Id) + 1.0 / (RrfK + i + 1);

        // Nad-pobieramy kandydatów przed dedupem po tekście: standardowe formułki (dyrektywy, tezy TSUE)
        // są cytowane dosłownie w wielu orzeczeniach — bez dedupu N kopii zajmuje N slotów top-K i przez
        // fuzję RRF wypycha realny przepis (np. właściwy artykuł kodeksu) poza wynik.
        var candidateIds = rrf.OrderByDescending(kv => kv.Value).Take(query.TopK * 4).Select(kv => kv.Key).ToList();
        if (candidateIds.Count == 0)
            return new RetrievalResult([], 0);

        var rows = await db.Chunks
            .Include(c => c.Document)
            .Where(c => candidateIds.Contains(c.Id))
            .ToListAsync(ct);

        var deduped = rows
            .Select(c => new RetrievedChunk
            {
                ChunkId = c.Id,
                DocumentId = c.DocumentId,
                Text = c.Text,
                Section = c.Section,
                Source = c.Document!.Source,
                DocType = c.Document.DocType,
                Title = c.Document.Title,
                SourceUrl = c.Document.SourceUrl,
                Locator = Deserialize(c.Locator),
                Score = rrf[c.Id],
                Similarity = sim.TryGetValue(c.Id, out var s) ? s : null,
            })
            .OrderByDescending(c => c.Score)
            .GroupBy(c => NormalizeForDedup(c.Text))   // kolaps identycznych tekstów — zostaje najwyżej scorowany
            .Select(g => g.First())
            .ToList();

        var maxSim = sim.Count > 0 ? sim.Values.Max() : 0;

        // Reranking (opcjonalny): cross-encoder przelicza trafność deduplikowanych kandydatów.
        // Gdy włączony, jego TOP-score steruje bramką abstynencji (MaxSimilarity) — jest lepiej
        // rozdzielony niż surowy cosine. Gdy wyłączony (reranker == null) — zachowanie jak dotąd.
        if (reranker is not null && deduped.Count > 0)
        {
            var scores = await reranker.RerankAsync(query.Text, deduped.Select(c => c.Text).ToList(), ct);
            var byIndex = scores.ToDictionary(x => x.Index, x => x.Score);
            var reranked = deduped
                .Select((c, i) => c with { RerankScore = byIndex.GetValueOrDefault(i) })
                .OrderByDescending(c => c.RerankScore ?? double.MinValue)
                .Take(query.TopK)
                .ToList();
            var topRerank = reranked.Count > 0 ? reranked[0].RerankScore ?? 0 : 0;
            return new RetrievalResult(reranked, topRerank);
        }

        return new RetrievalResult(deduped.Take(query.TopK).ToList(), maxSim);
    }

    private static IQueryable<ChunkEntity> ApplyFilters(IQueryable<ChunkEntity> q, RetrievalQuery query)
    {
        if (query.CourtType is { } ct) q = q.Where(c => c.Document!.CourtType == ct);
        if (query.DateFrom is { } from) q = q.Where(c => c.Document!.JudgmentDate >= from);
        if (query.DateTo is { } to) q = q.Where(c => c.Document!.JudgmentDate <= to);
        if (query.OnlyInForce) q = q.Where(c => c.Document!.DocType != "act" || c.Document!.InForce == true);
        if (query.MinChunkTokens > 0) q = q.Where(c => c.TokenCount >= query.MinChunkTokens);
        return q;
    }

    private static CitationLocator? Deserialize(JsonDocument? json) =>
        json is null ? null : json.Deserialize<CitationLocator>();

    /// <summary>Klucz dedupu: tekst bez różnic w białych znakach i wielkości liter.</summary>
    private static string NormalizeForDedup(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
