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

        // Ranking semantyczny (pełna lista kandydatów). Reranking (opcjonalny): cross-encoder przelicza
        // trafność; gdy włączony, jego TOP-score steruje bramką abstynencji (lepiej rozdzielony niż cosine).
        List<RetrievedChunk> ranked;
        double signal;
        if (reranker is not null && deduped.Count > 0)
        {
            var scores = await reranker.RerankAsync(query.Text, deduped.Select(c => c.Text).ToList(), ct);
            var byIndex = scores.ToDictionary(x => x.Index, x => x.Score);
            ranked = deduped
                .Select((c, i) => c with { RerankScore = byIndex.GetValueOrDefault(i) })
                .OrderByDescending(c => c.RerankScore ?? double.MinValue)
                .ToList();
            signal = ranked.Count > 0 ? ranked[0].RerankScore ?? 0 : 0;
        }
        else
        {
            ranked = deduped; // już posortowane po Score (RRF)
            signal = maxSim;
        }

        // QU-3: retrieval strukturalny — gdy pytanie zawiera cytat („art. 94 KW"), pobierz DOKŁADNIE ten
        // artykuł po metadanych i wstaw na górę (gwarantowane sloty). DOKŁADA, nigdy nie usuwa semantycznych;
        // brak rozpoznania aktu → zachowanie jak dziś (zero regresji).
        var structural = await StructuralAsync(query, ct);
        var final = structural.Concat(ranked)
            .GroupBy(c => c.ChunkId).Select(g => g.First()) // dedup; strukturalne (pierwsze) wygrywają slot
            .Take(query.TopK)
            .ToList();

        return new RetrievalResult(final, signal);
    }

    /// <summary>Dokładne trafienia po lokalizatorze dla cytatów wykrytych w pytaniu (QU-3). Omija
    /// <c>MinChunkTokens</c> (P5 — krótki § nie może wypaść) i pobiera CAŁY artykuł (P3).</summary>
    private async Task<List<RetrievedChunk>> StructuralAsync(RetrievalQuery query, CancellationToken ct)
    {
        var cites = CitationParser.Parse(query.Text);
        if (cites.Count == 0) return [];

        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var c in cites.Take(4))
        {
            var actExtId = await ResolveActAsync(c.ActHint, ct);
            if (actExtId is null) continue; // bez rozpoznanego aktu nie floodujemy art. N ze wszystkich kodeksów (P6)

            var hits = await db.Chunks.Include(x => x.Document)
                .Where(x => x.ArticleNo == c.Article && x.Document!.ExternalId == actExtId)
                .OrderBy(x => x.ChunkIndex)
                .Take(20)
                .ToListAsync(ct);

            foreach (var h in hits)
            {
                if (!seen.Add(h.Id)) continue;
                result.Add(new RetrievedChunk
                {
                    ChunkId = h.Id, DocumentId = h.DocumentId, Text = h.Text, Section = h.Section,
                    Source = h.Document!.Source, DocType = h.Document.DocType, Title = h.Document.Title,
                    SourceUrl = h.Document.SourceUrl, Locator = Deserialize(h.Locator),
                    Score = double.MaxValue, Similarity = null, // trafienie dokładne — zawsze na górę
                });
            }
        }
        return result;
    }

    /// <summary>Rozpoznaje akt z wskazówki: skrót (mapa aliasów → najkrótszy pasujący tytuł, np. KK≠KKW),
    /// fraza → dopasowanie rozmyte pg_trgm do tytułów aktów. Null = brak pewnego dopasowania (QU-2).</summary>
    private async Task<string?> ResolveActAsync(string? hint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;

        if (ActAliases.Canonical(hint) is { } canonical)
            return await db.Documents
                .Where(d => d.DocType == "act" && EF.Functions.ILike(d.Title, "%" + canonical + "%"))
                .OrderBy(d => d.Title.Length) // najkrótszy tytuł = właściwy kodeks (KK przed „KK wykonawczy")
                .Select(d => d.ExternalId)
                .FirstOrDefaultAsync(ct);

        var best = await db.Documents
            .Where(d => d.DocType == "act")
            .Select(d => new { d.ExternalId, Sim = EF.Functions.TrigramsSimilarity(d.Title, hint) })
            .OrderByDescending(x => x.Sim)
            .FirstOrDefaultAsync(ct);
        return best is not null && best.Sim >= 0.15 ? best.ExternalId : null;
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
