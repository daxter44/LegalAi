using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrawoRAG.Domain;
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
    /// (np. właściwy artykuł kodeksu). Nawet 400 (poprzednia wartość) tego nie zamykało: zmierzone
    /// 2026-08-12 na dwóch niezależnych chunkach (art. 56 KRO z 2026-07-23, art. 2 ustawy o opłatach
    /// abonamentowych) — dokładna ranga w całym korpusie #14 i #38 (dobre, konkurencyjne podobieństwo),
    /// a HNSW przy ef_search=400 nie widział ich NAWET w top-200/top-100. 1000 (maksimum dopuszczalne
    /// przez pgvector) wprowadza oba do puli TopK×4 — koszt +~7ms/zapytanie (10ms→17ms, zmierzone
    /// EXPLAIN ANALYZE), nieodczuwalne przy odpowiedziach liczonych w sekundach (embedding+reranker+LLM).
    /// </summary>
    private const int HnswEfSearch = 1000;

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
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        query.ReportStage("embed", "Zamieniam pytanie na wektor…");
        var qvec = new Vector(await LatencyLog.TimeAsync("embed", () => embedder.EmbedQueryAsync(query.Text, ct)));
        var k = query.CandidatesPerPath;

        // Tor gęsty: najmniejszy dystans cosine. Surowe SQL z rzutem na halfvec(1024) — IX_chunks_Embedding
        // jest indeksem wyrażeniowym (fp16, oszczędność pamięci przy budowie); LINQ CosineDistance
        // porównuje fp32 do fp32 i nie trafiłby w ten indeks (pełny sequential scan po 7M+ wierszy).
        //
        // Transakcja obejmuje TYLKO ten jeden tor, bo tylko on potrzebuje `SET LOCAL hnsw.ef_search`
        // (ustawienie musi obowiązywać na TYM SAMYM połączeniu co zapytanie — transakcja to gwarantuje,
        // a `LOCAL` sprząta po sobie, więc połączenie wraca do puli czyste). Wcześniej `tx` żył do końca
        // metody, czyli połączenie wisiało `idle in transaction` przez WSZYSTKIE pozostałe tory ORAZ dwa
        // round-tripy HTTP do cross-encodera (sekundy) — to zjada pulę połączeń przy równoległych
        // użytkownikach i blokuje autovacuum na chunks/documents.
        List<DenseHit> dense;
        query.ReportStage("dense", "Szukam znaczeniowo w bazie…", k);
        var denseSw = System.Diagnostics.Stopwatch.StartNew();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL hnsw.ef_search = {HnswEfSearch}", ct);
            dense = await DenseAsync(query, qvec, k, ct);
            await tx.CommitAsync(ct); // odczyt — commit tylko po to, żeby zamknąć transakcję jawnie
        }
        LatencyLog.Mark("dense", denseSw.ElapsedMilliseconds);

        // Tor rzadki: BM25 po tsvector (konfiguracja zgodna z kolumną generowaną).
        query.ReportStage("sparse", "Szukam po słowach kluczowych…", k);
        var sparse = await LatencyLog.TimeAsync("sparse", () => ApplyFilters(db.Chunks, query)
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, query.Text)))
            .Select(c => new { c.Id, Rank = c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery(PrawoRagDbContext.TextSearchConfig, query.Text)) })
            .OrderByDescending(x => x.Rank)
            .Take(k)
            .ToListAsync(ct));

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
        var acronymSw = System.Diagnostics.Stopwatch.StartNew();
        if (acronyms.Count > 0)
        {
            query.ReportStage("acronym", "Sprawdzam skróty i akronimy…", acronyms.Count);
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
                    //
                    // Dawniej stał tu SAVEPOINT, bo tor biegł WEWNĄTRZ transakcji obejmującej cały
                    // retrieval, a Postgres po błędzie zatruwa całą otaczającą transakcję (25P02
                    // „current transaction is aborted") — bez rollbacku padłyby wszystkie kolejne
                    // zapytania (zmierzone na żywo 2026-07-22). Po zawężeniu transakcji do samego toru
                    // gęstego ten tor biegnie BEZ transakcji: każde zapytanie jest własną, niejawną
                    // transakcją, więc nie ma czego zatruć ani cofać i sam try/catch już wystarcza.
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
                        // best-effort — ten tor tylko dokłada recall; brak wyników nie psuje odpowiedzi.
                    }
                }
            }
            finally
            {
                db.Database.SetCommandTimeout(originalTimeout);
            }
            LatencyLog.Mark("acronym", acronymSw.ElapsedMilliseconds);
        }

        // Nad-pobieramy kandydatów przed dedupem po tekście: standardowe formułki (dyrektywy, tezy TSUE)
        // są cytowane dosłownie w wielu orzeczeniach — bez dedupu N kopii zajmuje N slotów top-K i przez
        // fuzję RRF wypycha realny przepis (np. właściwy artykuł kodeksu) poza wynik.
        var candidateIds = rrf.OrderByDescending(kv => kv.Value).Take(query.TopK * 4).Select(kv => kv.Key).ToList();
        if (candidateIds.Count == 0)
            return new RetrievalResult([], 0);

        query.ReportStage("fetch_candidates", "Pobieram treść kandydatów…", candidateIds.Count);
        var rows = await LatencyLog.TimeAsync("fetch_candidates",
            () => Project(db.Chunks.Where(c => candidateIds.Contains(c.Id))).ToListAsync(ct));

        var deduped = rows
            .Select(c => new RetrievedChunk
            {
                ChunkId = c.Id,
                DocumentId = c.DocumentId,
                ChunkIndex = c.ChunkIndex,
                Text = c.Text,
                Section = c.Section,
                Source = c.Source,
                DocType = c.DocType,
                Title = c.Title,
                SourceUrl = c.SourceUrl,
                Locator = Deserialize(c.Locator),
                LegalBases = LegalBasesDisplay(c.TypedMetadata),
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
            query.ReportStage("rerank.main", "Oceniam trafność kandydatów…", deduped.Count);
            var scores = await LatencyLog.TimeAsync("rerank.main",
                () => reranker.RerankAsync(query.EffectiveRerankText, deduped.Select(c => c.Text).ToList(), ct));
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
        query.ReportStage("lane.signature", "Sprawdzam sygnatury orzeczeń…");
        var signature = await LatencyLog.TimeAsync("lane.signature", () => SignatureAsync(query, ct));

        // Lane odwołania do aktu: pytanie zawiera numer Dziennika Ustaw („Dz.U. 2025 poz. 1815"
        // albo bezpośrednio ELI „DU/2025/1815") → pobierz DOKŁADNIE ten akt. Sam poziom co sygnatura
        // orzeczenia (identyfikator dokumentu, nie zapytanie semantyczne) — bez re-embeddingu.
        query.ReportStage("lane.act_reference", "Sprawdzam odwołania do aktów…");
        var actReference = await LatencyLog.TimeAsync("lane.act_reference", () => ActReferenceAsync(query, ct));

        // QU-3: retrieval strukturalny — gdy pytanie zawiera cytat („art. 94 KW"), pobierz DOKŁADNIE ten
        // artykuł po metadanych i wstaw na górę (gwarantowane sloty). DOKŁADA, nigdy nie usuwa semantycznych;
        // brak rozpoznania aktu → zachowanie jak dziś (zero regresji).
        query.ReportStage("lane.structural", "Sprawdzam cytowane artykuły…");
        var structural = await LatencyLog.TimeAsync("lane.structural", () => StructuralAsync(query, ct));

        // Cap dominacji jednego dokumentu w torach DOKŁADNYCH: sygnatura/akt/cytat dociągają po
        // kilkanaście chunków JEDNEGO dokumentu ze Score=MaxValue — przy TopK=8 jedno trafienie zjadało
        // cały budżet (obserwacja użytkownika: „8 źródeł, same wyroki, zero ustawy"). Rezerwujemy kilka
        // slotów, żeby kontekst (semantyka, most cytowań) zawsze wszedł. Dedup po ChunkId PRZED capem,
        // żeby limit liczył realne chunki, nie duplikaty z nakładających się torów.
        var exact = ExactMatchCap.LimitPerDocument(
            signature.Concat(actReference).Concat(structural).GroupBy(c => c.ChunkId).Select(g => g.First()),
            ExactMatchCap.MaxPerDocument(query.TopK));

        // Most cytowań: przepis rządzący dociągnięty z cytowań w trafionych orzeczeniach. Głosują TYLKO
        // kandydaci istotni wg cross-encodera (BridgeVoterScoreFraction topu). Bez rerankera — wszyscy.
        var voterFloor = rerankTop * BridgeVoterScoreFraction;
        var voters = voterFloor is { } floor
            ? ranked.Where(c => c.RerankScore >= floor).ToList()
            : ranked;
        query.ReportStage("citation_bridge", "Dociągam przepisy z cytowań…");
        var bridge = await LatencyLog.TimeAsync("citation_bridge", () => CitationBridgeAsync(query, voters, ct));

        // Most NIE dostaje już gwarantowanego slotu przed semantyką: dociągnięty przepis przechodzi przez
        // tego samego sędziego co reszta i wchodzi tam, gdzie zasłużył. Wcześniej wstrzykiwał się na
        // wierzch z pominięciem rerankera, więc jeden fałszywy zwycięzca głosowania zajmował sloty [1..3].
        // Drugie wywołanie cross-encodera dotyczy garstki pasaży (≤ CitationBridgeArticles ×
        // BridgeChunksPerArticle) i tylko gdy most cokolwiek zwrócił.
        if (reranker is not null && bridge.Count > 0)
        {
            query.ReportStage("rerank.bridge", "Oceniam przepisy z cytowań…", bridge.Count);
            var bridgeScores = await LatencyLog.TimeAsync("rerank.bridge", () => reranker.RerankAsync(
                query.EffectiveRerankText, bridge.Select(c => c.Text).ToList(), ct));
            var byIndex = bridgeScores.ToDictionary(x => x.Index, x => x.Score);
            // Most PRZED `ranked` w konkatenacji: gdy ten sam chunk przyszedł oboma torami, ma zostać
            // wersja mostu (Score=double.MaxValue — po tym markerze testy i diagnostyka poznają, że to
            // most dociągnął przepis, a nie tor gęsty). Dopiero po dedupie sortujemy po sędzim.
            ranked = bridge
                .Select((c, i) => c with { RerankScore = byIndex.GetValueOrDefault(i) })
                .Concat(ranked)
                .GroupBy(c => c.ChunkId).Select(g => g.First())
                .OrderByDescending(c => c.RerankScore ?? double.MinValue)
                .ToList();
            bridge = [];   // most jest już wtopiony w `ranked` — nie dokładać go drugi raz
        }

        // Kolejność slotów: SYGNATURA/AKT (najbardziej konkretny ask) → cytat strukturalny → most → semantyka.
        var final = exact.Concat(bridge).Concat(ranked)
            .GroupBy(c => c.ChunkId).Select(g => g.First()) // dedup; wcześniejsze tory wygrywają slot
            .Take(query.TopK)
            .ToList();

        // ROZSZERZENIE SĄSIEDZTWA (plan SAS): gdy retrieval skoncentrował się na jednym AKCIE,
        // dociągnij sąsiadujące artykuły. Świadomie PO `Take(TopK)` i PO wyliczeniu capów — sąsiedztwo
        // nie konkuruje o sloty TopK, tylko je uzupełnia (`ExactMatchCap` istnieje, by jeden dokument
        // nie zjadł budżetu; tutaj dominacja aktu jest CELEM, nie awarią).
        // MOST VACATIO LEGIS: gdy w wyniku jest klauzula wejścia w życie wskazująca konkretne jednostki
        // („…wchodzą w życie art. 1 pkt 1 lit. a i c oraz pkt 3"), dociągnij TREŚĆ tych jednostek z tego
        // samego aktu. Świadomie tutaj — razem z sąsiedztwem, PO `Take(TopK)`: dociągnięta treść nie
        // konkuruje o sloty, bo bez niej odpowiedź jest niemożliwa (system odmawiał, mając samą klauzulę).
        var vacatio = await LatencyLog.TimeAsync("vacatio_legis", () => VacatioLegisAsync(query, final, ct));

        var neighbours = await NeighbourhoodAsync(query, final, ct);
        if (vacatio.Count > 0 || neighbours.Count > 0)
            final = final.Concat(vacatio).Concat(neighbours)
                .GroupBy(c => c.ChunkId).Select(g => g.First())
                // Akt ma czytać się LINIOWO — inaczej model dostaje przepisy w kolejności
                // podobieństwa, co przy kilkudziesięciu artykułach jest nie do czytania.
                .OrderBy(c => c.DocumentId).ThenBy(c => c.ChunkIndex)
                .ToList();

        // Ile FINALNYCH slotów zajęło trafienie dokładne (sygnatura/akt/cytat z pytania użytkownika) —
        // trzeci sygnał dla bramki abstynencji, obok cosine i score rerankera. Liczone po `final`, nie po
        // `exact`, bo interesuje nas to, co model FAKTYCZNIE dostanie w kontekście (cap per dokument
        // i TopK mogą uciąć). Most cytowań tu NIE wchodzi — jest sygnałem pochodnym, nie jawnym askiem
        // (patrz RetrievalResult.ExactMatchHits).
        var exactIds = exact.Select(c => c.ChunkId).ToHashSet();
        var exactInFinal = final.Count(c => exactIds.Contains(c.ChunkId));

        LatencyLog.Mark("retrieval.total", totalSw.ElapsedMilliseconds);
        return new RetrievalResult(final, maxSim, rerankTop, exactInFinal);
    }

    /// <summary>
    /// Dociąga TREŚĆ jednostek wskazanych w klauzuli wejścia w życie, która trafiła do wyniku.
    ///
    /// Dlaczego strukturalnie, a nie lepszym rankingiem (diagnoza 2026-08-27): treść nowelizacji ma dla
    /// pytania o datę rangi #2367/#50430/#82405 przy DOKŁADNYM skanie — pytanie niesie datę, a treść
    /// zmian nie niesie żadnej, więc w przestrzeni wektorowej nie ma czego mierzyć. Łącznik („art. 13
    /// wskazuje art. 1 pkt 3") jest CYTOWANIEM wewnątrz dokumentu, dokładnie jak w moście cytowań.
    ///
    /// Dwie decyzje warte zapamiętania:
    /// • dociągamy CAŁY wskazany artykuł tego samego dokumentu, a numery pkt/lit służą do KOLEJNOŚCI —
    ///   akt nowelizujący ma zwykle jeden wielki artykuł pocięty po rozmiarze, bez granulacji pkt/lit
    ///   w lokalizatorze (zmierzone: treść „pkt 1 lit. c" leży w chunku #2, „pkt 3" w #3);
    /// • szukamy w TYM SAMYM dokumencie (po <c>DocumentId</c>), więc rozpoznanie aktu jest niepotrzebne —
    ///   klauzula mówi o jednostkach własnego aktu, nie cudzego.
    /// </summary>
    private async Task<List<RetrievedChunk>> VacatioLegisAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> final, CancellationToken ct)
    {
        if (query.VacatioLegisChunks <= 0) return [];

        var clauses = final
            .Where(c => c.DocType == DocTypes.Act && VacatioLegis.IsEntryIntoForceClause(c.Text))
            .Select(c => (c.DocumentId, Targets: VacatioLegis.ParseTargets(c.Text)))
            .ToList();
        if (clauses.Count == 0) return [];

        var present = final.Select(c => c.ChunkId).ToHashSet();
        var result = new List<RetrievedChunk>();

        foreach (var (docId, targets) in clauses)
        {
            foreach (var target in targets)
            {
                if (result.Count >= query.VacatioLegisChunks) break;

                var rows = await Project(db.Chunks
                        .Where(x => x.DocumentId == docId && x.ArticleNo == target.Article)
                        .OrderBy(x => x.ChunkIndex))
                    .ToListAsync(ct);

                // Kolejność: najpierw chunki, w których stoi wskazany „pkt"/„lit." (to dokładnie te
                // przepisy, o które pyta użytkownik), potem pozostałe chunki artykułu — bo klauzula
                // wskazuje jednostki, których w lokalizatorze nie ma, a treść i tak trzeba pokazać.
                var markers = target.Markers().ToList();
                var ordered = rows
                    .OrderByDescending(r => markers.Count(m => r.Text.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(r => r.ChunkIndex);

                foreach (var row in ordered)
                {
                    if (result.Count >= query.VacatioLegisChunks) break;
                    if (!present.Add(row.Id)) continue; // już jest w wyniku albo już dociągnięty
                    result.Add(ExactMatchChunk(row));
                }
            }
        }

        return result;
    }

    /// <summary>Minimalna liczba NIEZALEŻNYCH orzeczeń cytujących artykuł, żeby wszedł mostem cytowań.
    /// Sygnał jest cienki (sonda: 10 cytowań w 30 chunkach) — kandydaci z 1 głosem to często śmieci
    /// (art. 822 KC o ubezpieczeniach dla pytania o delikt), a koszt wstrzyknięcia ZŁEGO przepisu
    /// (model ugruntuje się na nim) przewyższa koszt braku. Próg 2 na danych sondy przepuszcza
    /// wyłącznie normę właściwą (art. 415: 3 dokumenty; cała reszta po 1).</summary>
    private const int BridgeMinDocVotes = 2;

    /// <summary>
    /// Jaką część score'u NAJLEPSZEGO kandydata musi mieć pasaż, żeby głosować w moście cytowań.
    /// Dziś głosowali wszyscy po fuzji RRF — także pasaże ocenione na 0,17, które przegłosowały
    /// KK art. 64 (recydywa) w pytaniu o zgłoszenie wycieku danych (pomiar 2026-08-11). Próg jest
    /// WZGLĘDNY (ułamek topu), nie absolutny, z tego samego powodu, dla którego bramka abstynencji
    /// nie stoi na score rerankera: przy śmieciowej puli cross-encoder klastruje ~0,99 i absolutna
    /// liczba nic nie znaczy. Przy takiej puli próg względny nikogo nie odcina — degradacja do
    /// dzisiejszego zachowania, zamiast losowego cięcia. Bez rerankera głosują wszyscy, jak dotąd.
    /// </summary>
    private const double BridgeVoterScoreFraction = 0.5;

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
        // Wchłonięte nowelizacje poza torami SEMANTYCZNYMI (analiza 2026-09-01): ich krótkie chunki
        // wygrywają przestarzałą treścią z aktualnym tekstem jednolitym (np. stawka za nadgodziny
        // z 1996 r. zamiast art. 151(1) KP). Tory dokładne (sygnatura/ELI/cytat) i augmenter celowo
        // BEZ filtra — jawne wskazanie noweli (też jej przepisów przejściowych) nadal działa.
        // Bezpieczny rollout: kolumna startuje z false (filtr nic nie zmienia do czasu backfillu).
        conditions.Add("(d.\"DocType\" <> 'act' OR d.\"AbsorbedAmendment\" = false)");
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
        var keys = CaseNumberKey.Detect(query.EffectiveExactMatchText);
        if (keys.Count == 0) return [];

        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var key in keys.Take(3))
        {
            var hits = await Project(db.Chunks
                    .Where(x => x.Document!.CaseNumber == key)
                    .OrderBy(x => x.Document!.ExternalId).ThenBy(x => x.ChunkIndex)
                    .Take(SignatureChunksPerDoc))
                .ToListAsync(ct);

            foreach (var h in hits)
                if (seen.Add(h.Id)) result.Add(ExactMatchChunk(h));
        }
        return result;
    }

    /// <summary>Ile chunków aktu dociąga lane odwołania do Dziennika Ustaw — początek dokumentu
    /// (tytuł + pierwsze artykuły) po ChunkIndex; wystarcza, żeby model potwierdził że to WŁAŚCIWY
    /// akt i zacytował z niego, bez zalewania promptu całą treścią (akty bywają dłuższe niż orzeczenia).</summary>
    private const int ActReferenceChunksPerDoc = 15;

    /// <summary>
    /// Lane odwołania do aktu: pytanie zawiera numer Dziennika Ustaw („Dz.U. 2025 poz. 1815" albo
    /// bezpośrednio ELI „DU/2025/1815") → pobierz DOKŁADNIE ten akt po <c>documents.ExternalId</c>
    /// (naturalny klucz ingestii ELI, już indeksowany — <c>IX_documents_Source_ExternalId</c> — bez
    /// backfillu, bez re-embeddingu; ten sam wzorzec co <see cref="SignatureAsync"/> dla orzeczeń).
    /// Brak odwołania w pytaniu → pusto (zero kosztu).
    /// </summary>
    private async Task<List<RetrievedChunk>> ActReferenceAsync(RetrievalQuery query, CancellationToken ct)
    {
        var keys = ActEliKey.Detect(query.EffectiveExactMatchText);
        if (keys.Count == 0) return [];

        var result = new List<RetrievedChunk>();
        var seen = new HashSet<Guid>();
        foreach (var key in keys.Take(3))
        {
            var hits = await Project(db.Chunks
                    .Where(x => x.Document!.Source == "ELI" && x.Document.ExternalId == key)
                    .OrderBy(x => x.ChunkIndex)
                    .Take(ActReferenceChunksPerDoc))
                .ToListAsync(ct);

            foreach (var h in hits)
                if (seen.Add(h.Id)) result.Add(ExactMatchChunk(h));
        }
        return result;
    }

    /// <summary>Czytelne podstawy prawne z <c>referencedRegulations</c> (jsonb) — pole „text" każdego
    /// obiektu (wspólne dla SAOS i NSA). Cap 6, żeby karta była zwięzła. Null gdy brak.</summary>
    private static IReadOnlyList<string>? LegalBasesDisplay(JsonDocument? meta)
    {
        if (meta is null || meta.RootElement.ValueKind != JsonValueKind.Object ||
            !meta.RootElement.TryGetProperty("referencedRegulations", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return null;

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (list.Count >= 6) break;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("text", out var t) &&
                t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s)
                list.Add(s);
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Dokładne trafienia po lokalizatorze dla cytatów wykrytych w pytaniu (QU-3). Omija
    /// <c>MinChunkTokens</c> (P5 — krótki § nie może wypaść) i pobiera CAŁY artykuł (P3).</summary>
    private async Task<List<RetrievedChunk>> StructuralAsync(RetrievalQuery query, CancellationToken ct)
    {
        var cites = CitationParser.Parse(query.EffectiveExactMatchText);
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
        var hits = await Project(db.Chunks
                .Where(x => x.ArticleNo == article && x.Document!.ExternalId == actExtId)
                .OrderBy(x => x.ChunkIndex)
                .Take(maxChunks))
            .ToListAsync(ct);

        foreach (var h in hits)
            if (seen.Add(h.Id)) result.Add(ExactMatchChunk(h));
    }

    /// <summary>
    /// Wiersz chunka W PROJEKCJI — wyłącznie kolumny, których retrieval faktycznie używa.
    ///
    /// Powód: encja <see cref="ChunkEntity"/> niesie `Embedding` (1024×fp32 ≈ 4 KB) i `SearchVector`
    /// (tsvector długiego chunka to kolejne kilka KB). Pobieranie pełnych, ŚLEDZONYCH encji ciągnęło
    /// więc setki KB wektorów na turę czatu — dla danych, których nikt tu nie czyta (dystanse policzył
    /// już Postgres w torze gęstym) — plus snapshoty change trackera. Projekcja do typu innego niż
    /// encja wyłącza śledzenie z definicji, więc `AsNoTracking` jest tu zbędne.
    /// </summary>
    private sealed record ChunkRow(
        Guid Id, Guid DocumentId, int ChunkIndex, string Text, string? Section, JsonDocument? Locator,
        string Source, string DocType, string Title, string? SourceUrl, JsonDocument? TypedMetadata);

    /// <summary>
    /// Dociąga SĄSIEDNIE artykuły wokół trafień w dominującym akcie (plan SAS, Zadanie 2).
    ///
    /// Powód (zmierzony przypadek): pytanie o „limity wpłat na OKI" dało 8 z 8 źródeł z właściwej
    /// ustawy i ANI JEDNEGO z limitem — bo ustawa nazywa go „progiem zwolnienia". Kryterium wyboru
    /// jest ślepe na synonim ustawowy, więc dołożenie kolejnych PODOBNYCH chunków nic nie da.
    /// Sąsiedztwo działa, bo w tekstach prawnych progi i wyjątki stoją przy przepisie, który
    /// modyfikują.
    ///
    /// Budżet tokenów (<see cref="RetrievalQuery.NeighbourhoodTokenBudget"/>) to CAŁA obsługa
    /// przypadku „kodeks": dla 18-stronicowej ustawy obejmuje w praktyce cały akt, dla kodeksu
    /// cywilnego ucina do okolic trafień. Zamiast osobnej gałęzi „czy cały akt się mieści".
    ///
    /// Odsiew po najbliższości do trafień: przy przekroczeniu budżetu zostają artykuły NAJBLIŻSZE
    /// trafieniom — one najpewniej niosą powiązany przepis.
    /// </summary>
    private async Task<List<RetrievedChunk>> NeighbourhoodAsync(
        RetrievalQuery query, IReadOnlyList<RetrievedChunk> final, CancellationToken ct)
    {
        var ranges = ArticleNeighbourhood.Plan(
            final, query.NeighbourhoodMinChunks, query.NeighbourhoodRadius);
        if (ranges.Count == 0) return [];

        query.ReportStage("neighbourhood", "Dociągam sąsiednie artykuły ustawy…", ranges.Count);

        var have = final.Select(c => c.ChunkId).ToHashSet();
        // Pozycje trafień per dokument — do sortowania po odległości przy cięciu budżetem.
        var hits = final
            .GroupBy(c => c.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.ChunkIndex).ToList());

        var fetched = await LatencyLog.TimeAsync("neighbourhood", async () =>
        {
            var acc = new List<ChunkRow>();
            // Zapytanie per zakres — trafia w unikalny indeks (DocumentId, ChunkIndex), więc to
            // odczyt po zakresie na indeksie, nie skan. Zakresów jest tyle, ile rozsianych trafień.
            foreach (var r in ranges)
            {
                var rows = await Project(db.Chunks.Where(c =>
                        c.DocumentId == r.DocumentId &&
                        c.ChunkIndex >= r.FromIndex && c.ChunkIndex <= r.ToIndex))
                    .ToListAsync(ct);
                acc.AddRange(rows);
            }
            return acc;
        });

        var budget = query.NeighbourhoodTokenBudget;
        var result = new List<RetrievedChunk>();

        foreach (var row in fetched
            .Where(r => !have.Contains(r.Id))
            .DistinctBy(r => r.Id)
            .OrderBy(r => DistanceToNearestHit(r, hits)))
        {
            // TokenCount czytamy z bazy osobno (Project go nie niesie) — patrz TokenCounts niżej.
            if (budget <= 0) break;
            result.Add(NeighbourChunk(row));
            budget -= EstimatedTokens(row);
        }

        return result;
    }

    /// <summary>Odległość artykułu od NAJBLIŻSZEGO trafienia w tym samym dokumencie — im mniejsza,
    /// tym większa szansa, że niesie przepis powiązany z pytaniem.</summary>
    private static int DistanceToNearestHit(ChunkRow row, Dictionary<Guid, List<int>> hits) =>
        hits.TryGetValue(row.DocumentId, out var positions)
            ? positions.Min(p => Math.Abs(p - row.ChunkIndex))
            : int.MaxValue;

    /// <summary>
    /// Szacunek tokenów z długości tekstu (~4 znaki/token) — świadomie NIE dociągamy
    /// <c>TokenCount</c> z bazy dodatkową kolumną: budżet jest zgrubnym hamulcem na rozmiar promptu,
    /// a nie pomiarem, więc dokładność co do tokena nic tu nie kupuje, a rozszerzyłaby projekcję
    /// używaną przez WSZYSTKIE tory.
    /// </summary>
    private static int EstimatedTokens(ChunkRow row) => Math.Max(1, row.Text.Length / 4);

    /// <summary>
    /// Chunk dociągnięty SĄSIEDZTWEM. <c>Score = double.MinValue</c> to marker pochodzenia
    /// (symetrycznie do <c>MaxValue</c> torów dokładnych) — po nim testy i diagnostyka poznają, że
    /// artykuł nie wygrał rankingu, tylko przyszedł jako kontekst obok trafienia.
    /// </summary>
    private static RetrievedChunk NeighbourChunk(ChunkRow h) => new()
    {
        ChunkId = h.Id, DocumentId = h.DocumentId, ChunkIndex = h.ChunkIndex,
        Text = h.Text, Section = h.Section,
        Source = h.Source, DocType = h.DocType, Title = h.Title, SourceUrl = h.SourceUrl,
        Locator = Deserialize(h.Locator),
        LegalBases = LegalBasesDisplay(h.TypedMetadata),
        Score = double.MinValue, Similarity = null,
    };

    /// <summary>Projekcja wspólna dla wszystkich torów — jedno miejsce, w którym rośnie lista kolumn.</summary>
    private static IQueryable<ChunkRow> Project(IQueryable<ChunkEntity> q) =>
        q.Select(c => new ChunkRow(
            c.Id, c.DocumentId, c.ChunkIndex, c.Text, c.Section, c.Locator,
            c.Document!.Source, c.Document.DocType, c.Document.Title, c.Document.SourceUrl,
            c.Document.TypedMetadata));

    /// <summary>
    /// Chunk z toru DOKŁADNEGO (sygnatura / odwołanie do aktu / cytat artykułu) — wspólny mapper dla
    /// trzech torów, które konstruowały ten sam obiekt trzema kopiami tego samego kodu.
    /// `Score = double.MaxValue` to marker „trafienie dokładne" (po nim testy i diagnostyka poznają
    /// pochodzenie chunka), `Similarity = null` bo tor dokładny nie liczy cosine.
    /// </summary>
    private static RetrievedChunk ExactMatchChunk(ChunkRow h) => new()
    {
        ChunkId = h.Id, DocumentId = h.DocumentId, ChunkIndex = h.ChunkIndex,
        Text = h.Text, Section = h.Section,
        Source = h.Source, DocType = h.DocType, Title = h.Title, SourceUrl = h.SourceUrl,
        Locator = Deserialize(h.Locator),
        // Podstawy prawne dotyczą orzeczeń (lane sygnatury). Dla aktów (lane odwołania, cytat, most)
        // metadane nie mają `referencedRegulations`, więc wychodzi null — tak jak dotąd.
        LegalBases = LegalBasesDisplay(h.TypedMetadata),
        Score = double.MaxValue, Similarity = null,
    };

    /// <summary>
    /// Memoizacja rozpoznania aktu w obrębie JEDNEJ instancji retrievera (rejestracja `AddScoped`, więc
    /// zasięg = jedno żądanie; przy follow-upie ten sam obiekt obsługuje OBA retrievale).
    ///
    /// Powód: `ResolveActAsync` to najdroższe zapytanie poza torem gęstym — gałąź aliasu robi
    /// `ILIKE '%…%'`, a gałąź frazy liczy `similarity()` dla KAŻDEGO aktu w korpusie (brak indeksu GIN
    /// trgm na `documents.Title`; migracja zakłada samo rozszerzenie pg_trgm). Wywołań na retrieval jest
    /// do 6: do 4 z toru strukturalnego + do 2 z mostu cytowań — i praktycznie zawsze o TĘ SAMĄ wskazówkę
    /// („art. 415 KC" i „art. 5 KC" → dwa razy „KC"; zwycięzcy mostu też zwykle dzielą alias).
    ///
    /// Klucz CELOWO `Ordinal` (wielkość liter znaczy): `ILIKE` jest nieczuły na wielkość, ale
    /// `similarity()` już nie — wspólny wpis dla „KC" i „kc" mógłby zwrócić wynik, którego baza dla
    /// danego zapisu nie dałaby. Memoizacja ma być niewidoczna, nie „prawie niewidoczna".
    ///
    /// Cache PROCESOWY (zero zapytań po rozgrzaniu) byłby jeszcze tańszy, ale wymaga unieważniania po
    /// ingeście aktów — inaczej zapamiętany NULL trwale ukrywa akt dodany później. To osobny krok, do
    /// zrobienia z pomiarem w ręku, nie po drodze.
    /// </summary>
    private readonly Dictionary<string, string?> _actResolutionCache = new(StringComparer.Ordinal);

    /// <summary>Rozpoznaje akt z wskazówki: skrót (mapa aliasów → najkrótszy pasujący tytuł, np. KK≠KKW),
    /// fraza → dopasowanie rozmyte pg_trgm do tytułów aktów. Null = brak pewnego dopasowania (QU-2).</summary>
    private async Task<string?> ResolveActAsync(string? hint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        if (_actResolutionCache.TryGetValue(hint, out var cached)) return cached;

        var resolved = await ResolveActUncachedAsync(hint, ct);
        _actResolutionCache[hint] = resolved;
        return resolved;
    }

    private async Task<string?> ResolveActUncachedAsync(string hint, CancellationToken ct)
    {
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
        // Wchłonięte nowelizacje poza torami semantycznymi — lustro warunku z DenseAsync (analiza 2026-09-01).
        q = q.Where(c => c.Document!.DocType != "act" || !c.Document!.AbsorbedAmendment);
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
