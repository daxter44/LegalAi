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

    /// <summary>Ile torów akronimowych maksymalnie (JAK-5b) — pytania mają zwykle 0–1 akronim;
    /// limit chroni przed pytaniem-listą skrótów.</summary>
    private const int MaxAcronymLanes = 2;

    /// <summary>Krótki, dedykowany timeout dla toru akronimowego — nie globalny 30s. Zmierzone na
    /// żywo: pospolite słowo złapane heurystyką (np. „UMOWA") dopasowuje setki tysięcy chunków,
    /// ORDER BY ts_rank po takim zbiorze jest kosztowny. Prawdziwy akronim (rzadkie słowo, np.
    /// „KSeF" — zmierzone 130ms) kończy się w ułamku sekundy; 3s to hojny margines, nie próg dla
    /// dobrego przypadku. Krótszy timeout = szybsza degradacja zamiast marnowania 30s per fałszywe
    /// trafienie (dotkliwe przy wielu wywołaniach pod rząd, np. analiza dokumentu per jednostka).</summary>
    private static readonly TimeSpan AcronymLaneTimeout = TimeSpan.FromSeconds(3);

    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken ct)
    {
        var qvec = new Vector(await embedder.EmbedQueryAsync(query.Text, ct));
        var k = query.CandidatesPerPath;

        // ef_search musi obowiązywać na TYM samym połączeniu co zapytanie dense — transakcja to gwarantuje.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL hnsw.ef_search = {HnswEfSearch}", ct);

        // Tor gęsty: najmniejszy dystans cosine. Surowe SQL z rzutem na halfvec(1024) — IX_chunks_Embedding
        // jest teraz indeksem wyrażeniowym (fp16, oszczędność pamięci przy budowie); LINQ CosineDistance
        // porównuje fp32 do fp32 i nie trafiłby w ten indeks (pełny sequential scan po 7M+ wierszy).
        var dense = await DenseAsync(query, qvec, k, ct);

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

        // JAK-5b: tor akronimowy (Case 4 — „KSeF"). websearch_to_tsquery AND-uje wszystkie słowa
        // pytania, więc chunki zawierające akronim, ale nie resztę słów, wypadały z toru rzadkiego,
        // a embedding nie generalizuje skrótu na pełną nazwę. Osobne, JEDNOTOKENOWE zapytanie
        // leksykalne per wykryty akronim wchodzi do fuzji RRF jak każdy tor — o precyzję dba dalej
        // reranking/fuzja, my łatamy wyłącznie dziurę recall. Brak akronimów w pytaniu = zero kosztu.
        var acronyms = AcronymDetector.Extract(query.Text).Take(MaxAcronymLanes).ToList();
        if (acronyms.Count > 0)
        {
            // Krótki timeout TYLKO na czas toru akronimowego — przywrócony niezależnie od wyniku,
            // żeby finalny fetch chunków (poniżej) miał normalny, pełny limit.
            var originalTimeout = db.Database.GetCommandTimeout();
            db.Database.SetCommandTimeout(AcronymLaneTimeout);
            try
            {
                foreach (var acronym in acronyms)
                {
                    // Fail-open: heurystyka detektora bywa fałszywym trafieniem na zwykłe słowo pisane
                    // WIELKIMI LITERAMI (zaobserwowane na żywo: „UMOWA" — dopasowuje setki tysięcy
                    // chunków, ORDER BY ts_rank po takim zbiorze jest kosztowne). To tylko dodatkowy
                    // sygnał recall (komentarz wyżej) — awaria tego toru NIE MOŻE wywalić całej
                    // odpowiedzi czatu, tak jak awaria augmentera już jest best-effort.
                    // SAVEPOINT jest KONIECZNY: sam try/catch nie wystarczy — Postgres po błędzie
                    // zatruwa CAŁĄ otaczającą transakcję (25P02 „current transaction is aborted"),
                    // więc bez rollbacku do savepointu każde KOLEJNE zapytanie w tej transakcji
                    // (drugi akronim, finalny fetch chunków) też by padło (zmierzone na żywo, 2026-07-22).
                    const string savepoint = "acronym_lane";
                    await tx.CreateSavepointAsync(savepoint, ct);
                    try
                    {
                        var acrHits = await ApplyFilters(db.Chunks, query)
                            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, acronym)))
                            .Select(c => new { c.Id, Rank = c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, acronym)) })
                            .OrderByDescending(x => x.Rank)
                            .Take(k)
                            .ToListAsync(ct);
                        for (var i = 0; i < acrHits.Count; i++)
                            rrf[acrHits[i].Id] = rrf.GetValueOrDefault(acrHits[i].Id) + 1.0 / (RrfK + i + 1);
                    }
                    catch (Exception) when (ct.IsCancellationRequested == false)
                    {
                        // best-effort — cofnij TYLKO ten tor (savepoint), reszta transakcji zostaje ważna.
                        await tx.RollbackToSavepointAsync(savepoint, ct);
                    }
                }
            }
            finally
            {
                db.Database.SetCommandTimeout(originalTimeout);
            }
        }

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

        // Ranking semantyczny (pełna lista kandydatów). Reranking (opcjonalny): cross-encoder ustawia
        // KOLEJNOŚĆ źródeł; jego top-score wraca OSOBNYM sygnałem (RerankTopScore) — NIE nadpisuje
        // MaxSimilarity. Bramka abstynencji zostaje na cosine: stabilna skala pod kalibrację progu,
        // a score rerankera („najlepszy z podanych") klastruje ~0,99 nawet na śmieciowej puli
        // (zmierzone w raporcie odmów 2026-07-20). Jeśli kalibracja kiedyś pokaże, że rerank score
        // rozdziela lepiej — przełączenie to jedna linia TUTAJ, z danymi w ręku, nie cichy skutek
        // uboczny włączenia rerankera.
        List<RetrievedChunk> ranked;
        double? rerankTop = null;
        if (reranker is not null && deduped.Count > 0)
        {
            var scores = await reranker.RerankAsync(query.Text, deduped.Select(c => c.Text).ToList(), ct);
            var byIndex = scores.ToDictionary(x => x.Index, x => x.Score);
            ranked = deduped
                .Select((c, i) => c with { RerankScore = byIndex.GetValueOrDefault(i) })
                .OrderByDescending(c => c.RerankScore ?? double.MinValue)
                .ToList();
            rerankTop = ranked.Count > 0 ? ranked[0].RerankScore : null;
        }
        else
        {
            ranked = deduped; // już posortowane po Score (RRF)
        }

        // Sygnatura akt: gdy pytanie zawiera sygnaturę („III SA/Po 154/26"), pobierz DOKŁADNIE to
        // orzeczenie po znormalizowanym kluczu i wstaw na SAM WIERZCH. Sygnatura to identyfikator,
        // nie zapytanie semantyczne — similarity nigdy tego nie gwarantuje (własna sygnatura ląduje
        // w tekście chunka tylko przypadkiem). DOKŁADA, nie usuwa; brak sygnatury → zero kosztu.
        var signature = await SignatureAsync(query, ct);

        // QU-3: retrieval strukturalny — gdy pytanie zawiera cytat („art. 94 KW"), pobierz DOKŁADNIE ten
        // artykuł po metadanych i wstaw na górę (gwarantowane sloty). DOKŁADA, nigdy nie usuwa semantycznych;
        // brak rozpoznania aktu → zachowanie jak dziś (zero regresji).
        var structural = await StructuralAsync(query, ct);

        // Most cytowań: przepis rządzący dociągnięty z cytowań w trafionych orzeczeniach.
        // Sloty PO strukturalnych (jawny cytat użytkownika wygrywa), PRZED semantycznymi (norma jako
        // kotwica na początku listy źródeł). Sygnał abstynencji liczony wyżej — most go NIE dotyka
        // (tylko dokłada źródła; nie może zamienić odmowy w odpowiedź).
        var bridge = await CitationBridgeAsync(query, deduped, ct);
        // Kolejność slotów: SYGNATURA (najbardziej konkretny ask) → cytat strukturalny → most → semantyka.
        var final = signature.Concat(structural).Concat(bridge).Concat(ranked)
            .GroupBy(c => c.ChunkId).Select(g => g.First()) // dedup; wcześniejsze tory wygrywają slot
            .Take(query.TopK)
            .ToList();

        return new RetrievalResult(final, maxSim, rerankTop);
    }

    /// <summary>Minimalna liczba NIEZALEŻNYCH orzeczeń cytujących artykuł, żeby wszedł mostem cytowań.
    /// Sygnał jest cienki (sonda: 10 cytowań w 30 chunkach) — kandydaci z 1 głosem to często śmieci
    /// (art. 822 KC o ubezpieczeniach dla pytania o delikt), a koszt wstrzyknięcia ZŁEGO przepisu
    /// (model ugruntuje się na nim) przewyższa koszt braku. Próg 2 na danych sondy przepuszcza
    /// wyłącznie normę właściwą (art. 415: 3 dokumenty; cała reszta po 1).</summary>
    private const int BridgeMinDocVotes = 2;

    /// <summary>Ile chunków jednego artykułu most może dołożyć (przepisy to zwykle 1–3 chunki;
    /// limit chroni budżet promptu przed artykułami-tasiemcami).</summary>
    private const int BridgeChunksPerArticle = 6;

    /// <summary>
    /// Most cytowań (diagnoza 2026-07-17 + sonda --probe-akty): dla pytań opisowych przepis rządzący
    /// jest nieretrievalny (przegrywa podobieństwo z narracjami orzeczeń; w puli samych aktów wygrywa
    /// pułapka leksykalna — act-only lane obalony pomiarem). Ale trafione orzeczenia SAME cytują normę,
    /// na której się opierają („na podstawie art. 415 k.c.") — sąd zrobił mapowanie stan faktyczny→przepis
    /// lepiej niż jakikolwiek embedding. Parsujemy więc teksty kandydatów-orzeczeń (już w pamięci — zero
    /// dodatkowego retrievalu), głosowanie per NIEZALEŻNY dokument, próg+cap, dociągnięcie tekstu artykułu
    /// po metadanych. Świadomie NIE parsujemy pełnych dokumentów (sąsiednie chunki): każde uzasadnienie
    /// cytuje art. 98/108 KPC (koszty procesu) — wygrałyby każde głosowanie; chunki trafione semantycznie
    /// cytują przepisy kontekstowo trafne.
    /// </summary>
    private async Task<List<RetrievedChunk>> CitationBridgeAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> candidates, CancellationToken ct)
    {
        if (query.CitationBridgeArticles <= 0) return [];

        var winners = candidates
            .Where(c => c.DocType != "act") // akty cytujące inne akty to nie jest głos orzecznictwa
            .SelectMany(c => JudgmentCitationParser.Parse(c.Text)
                .Where(cite => cite.Alias is not null)
                .Select(cite => (cite.Alias, cite.Article, c.DocumentId)))
            .GroupBy(x => (x.Alias, x.Article))
            .Select(g => (g.Key.Alias, g.Key.Article, Docs: g.Select(x => x.DocumentId).Distinct().Count(), Total: g.Count()))
            .Where(x => x.Docs >= BridgeMinDocVotes)
            .OrderByDescending(x => x.Docs).ThenByDescending(x => x.Total)
            .Take(query.CitationBridgeArticles)
            .ToList();

        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var w in winners)
        {
            var actExtId = await ResolveActAsync(w.Alias, ct);
            if (actExtId is null) continue;
            await FetchArticleAsync(w.Article, actExtId, BridgeChunksPerArticle, seen, result, ct);
        }
        return result;
    }

    /// <summary>Tor gęsty przez surowe SQL: kolumna <c>Embedding</c> zostaje fp32 (przechowywanie), ale
    /// dystans liczony jest po rzucie obu stron na <c>halfvec(1024)</c>, żeby zapytanie trafiało w
    /// wyrażeniowy indeks HNSW <c>IX_chunks_Embedding</c> (zbudowany na <c>Embedding::halfvec(1024)</c>).</summary>
    private async Task<List<DenseHit>> DenseAsync(RetrievalQuery query, Vector qvec, int k, CancellationToken ct)
    {
        var parameters = new List<object>();
        string P(object value) { parameters.Add(value); return $"{{{parameters.Count - 1}}}"; }

        var qvecPlaceholder = P(qvec);
        var conditions = new List<string> { "c.\"Embedding\" IS NOT NULL" };
        if (query.CourtType is { } courtType) conditions.Add($"d.\"CourtType\" = {P(courtType)}");
        if (query.DateFrom is { } from) conditions.Add($"d.\"JudgmentDate\" >= {P(from)}");
        if (query.DateTo is { } to) conditions.Add($"d.\"JudgmentDate\" <= {P(to)}");
        if (query.OnlyInForce) conditions.Add("(d.\"DocType\" <> 'act' OR d.\"InForce\" = true)");
        if (query.MinChunkTokens > 0) conditions.Add($"c.\"TokenCount\" >= {P(query.MinChunkTokens)}");
        // SAOS judgmentType=REGULATION — patrz komentarz w ApplyFilters. IS DISTINCT FROM (nie <>), żeby
        // NULL (brak klucza — akty, orzeczenia sprzed dodania metadanych) przechodził filtr, nie znikał.
        conditions.Add("d.\"TypedMetadata\"->>'judgmentType' IS DISTINCT FROM 'REGULATION'");
        var limitPlaceholder = P(k);

        var sql = $"""
            SELECT c."Id" AS "Id", (c."Embedding"::halfvec(1024) <=> {qvecPlaceholder}::halfvec(1024)) AS "Dist"
            FROM chunks c
            JOIN documents d ON d."Id" = c."DocumentId"
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY "Dist"
            LIMIT {limitPlaceholder}
            """;

        return await db.Database.SqlQueryRaw<DenseHit>(sql, parameters.ToArray()).ToListAsync(ct);
    }

    private sealed record DenseHit(Guid Id, double Dist);

    /// <summary>Ile chunków jednego orzeczenia dociąga lane sygnatury — początek dokumentu (sentencja
    /// + start uzasadnienia) po ChunkIndex; tam jest rozstrzygnięcie, którego szuka prawnik.</summary>
    private const int SignatureChunksPerDoc = 12;

    /// <summary>
    /// Lane sygnatury: pytanie zawiera sygnaturę akt → pobierz DOKŁADNIE to orzeczenie po
    /// znormalizowanym kluczu (<c>documents.CaseNumber</c>, indeks) i wstaw na wierzch. To retrieval
    /// STRUKTURALNY (exact-match), nie semantyczny — bez re-embeddingu, działa też na istniejącym
    /// korpusie (SAOS) po backfillu kolumny. Brak sygnatury w pytaniu → pusto (zero kosztu).
    /// </summary>
    private async Task<List<RetrievedChunk>> SignatureAsync(RetrievalQuery query, CancellationToken ct)
    {
        var keys = CaseNumberKey.Detect(query.Text);
        if (keys.Count == 0) return [];

        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var key in keys.Take(3))
        {
            var hits = await db.Chunks.Include(x => x.Document)
                .Where(x => x.Document!.CaseNumber == key)
                .OrderBy(x => x.Document!.ExternalId).ThenBy(x => x.ChunkIndex)
                .Take(SignatureChunksPerDoc)
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
            await FetchArticleAsync(c.Article, actExtId, 20, seen, result, ct);
        }
        return result;
    }

    /// <summary>Pobiera chunki DOKŁADNIE tego artykułu po metadanych (wspólne dla toru strukturalnego
    /// i mostu cytowań). Omija <c>MinChunkTokens</c> (P5 — krótki § nie może wypaść).</summary>
    private async Task FetchArticleAsync(
        string article, string actExtId, int maxChunks, HashSet<Guid> seen, List<RetrievedChunk> result, CancellationToken ct)
    {
        var hits = await db.Chunks.Include(x => x.Document)
            .Where(x => x.ArticleNo == article && x.Document!.ExternalId == actExtId)
            .OrderBy(x => x.ChunkIndex)
            .Take(maxChunks)
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
        // SAOS judgmentType=REGULATION (zarządzenie porządkowe, np. "doręczyć odpis pełnomocnikowi") —
        // czysto kancelaryjne, zero treści merytorycznej, a krótkie niemal-identyczne teksty tworzą
        // sztucznie „lepki" klaster w przestrzeni embeddingów (zmierzone: similarity 0,84 do niezwiązanego
        // pytania). Nigdy nie niesie wartości dla RAG — wykluczone bezwarunkowo, nie flagą.
        q = q.Where(c => c.Document!.DocType != "judgment" || c.Document!.TypedMetadata == null ||
            c.Document!.TypedMetadata.RootElement.GetProperty("judgmentType").GetString() != "REGULATION");
        return q;
    }

    private static CitationLocator? Deserialize(JsonDocument? json) =>
        json is null ? null : json.Deserialize<CitationLocator>();

    /// <summary>Klucz dedupu: tekst bez różnic w białych znakach i wielkości liter.</summary>
    private static string NormalizeForDedup(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
