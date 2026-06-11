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
public sealed class HybridRetriever(PrawoRagDbContext db, IEmbeddingProvider embedder) : IRetriever
{
    private const int RrfK = 60;

    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        var qvec = new Vector(await embedder.EmbedQueryAsync(query.Text, ct));
        var k = query.CandidatesPerPath;

        var filtered = ApplyFilters(db.Chunks.Where(c => c.Embedding != null), query);

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

        var topIds = rrf.OrderByDescending(kv => kv.Value).Take(query.TopK).Select(kv => kv.Key).ToList();
        if (topIds.Count == 0)
            return new RetrievalResult([], 0);

        var rows = await db.Chunks
            .Include(c => c.Document)
            .Where(c => topIds.Contains(c.Id))
            .ToListAsync(ct);

        var chunks = rows
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
            .ToList();

        var maxSim = sim.Count > 0 ? sim.Values.Max() : 0;
        return new RetrievalResult(chunks, maxSim);
    }

    private static IQueryable<ChunkEntity> ApplyFilters(IQueryable<ChunkEntity> q, RetrievalQuery query)
    {
        if (query.CourtType is { } ct) q = q.Where(c => c.Document!.CourtType == ct);
        if (query.DateFrom is { } from) q = q.Where(c => c.Document!.JudgmentDate >= from);
        if (query.DateTo is { } to) q = q.Where(c => c.Document!.JudgmentDate <= to);
        if (query.OnlyInForce) q = q.Where(c => c.Document!.DocType != "act" || c.Document!.InForce == true);
        return q;
    }

    private static CitationLocator? Deserialize(JsonDocument? json) =>
        json is null ? null : json.Deserialize<CitationLocator>();
}
