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
        // trafiły do wyników zwykłym retrievalem.
        var unabsorbedDates = await BuildUnabsorbedDatesAsync(ct);

        // JEDNO zapytanie o dokumenty-akty z wyników: `ExternalId` (do oznaczania nowel) i `TypedMetadata`
        // (do wyłuskania ich nowel niżej). Wcześniej te same wiersze pobierano DWA razy, przy czym drugi
        // raz jako pełne, ŚLEDZONE encje — z całym jsonb metadanych i resztą kolumn.
        var actDocs = await db.Documents
            .Where(d => actDocIds.Contains(d.Id))
            .Select(d => new { d.Id, d.ExternalId, d.TypedMetadata })
            .ToListAsync(ct);
        var extIdByDocId = actDocs.ToDictionary(x => x.Id, x => x.ExternalId);

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

        var seen = new HashSet<Guid>(tagged.Select(c => c.ChunkId));

        // Capy (raport odmów 2026-07-18): augmentacja działa NA finalnej liście TopK, więc bez limitu
        // potrafiła dołożyć 8+ fragmentów jednej szerokiej ustawy zmieniającej (11 i 17 źródeł przy
        // TopK=8), rozcieńczając trafną normę i prowadząc do odmowy treściowej modelu.
        var totalAdded = 0;

        foreach (var d in actDocs)
        {
            if (totalAdded >= MaxFragmentsTotal) break;
            var amendments = ParseUnabsorbed(d.TypedMetadata);
            if (amendments.Count == 0) continue;

            var articles = new HashSet<string>(citedArticles, StringComparer.OrdinalIgnoreCase);
            if (articlesByDoc.TryGetValue(d.Id, out var arts)) articles.UnionWith(arts);
            if (articles.Count == 0) continue;

            foreach (var am in amendments)
            {
                if (totalAdded >= MaxFragmentsTotal) break;
                // Projekcja, nie encje: `ChunkEntity` niesie `Embedding` (1024×fp32 ≈ 4 KB/chunk)
                // i `SearchVector` — tu nieużywane. Świadomie BEZ `Take`: dopasowanie po treści diffu
                // (niżej) może trafić dowolny chunk noweli, więc obcięcie listy zmieniałoby WYNIKI,
                // a nie tylko koszt. Ograniczamy szerokość wiersza, nie liczbę wierszy.
                var amChunks = await db.Chunks
                    .Where(c => c.Document!.ExternalId == am.EliId)
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => new AmendmentChunkRow(
                        c.Id, c.DocumentId, c.Text, c.Section, c.Locator,
                        c.Document!.Source, c.Document.DocType, c.Document.Title, c.Document.SourceUrl))
                    .ToListAsync(ct);
                var perAmendment = 0;
                foreach (var ch in amChunks)
                {
                    if (perAmendment >= MaxFragmentsPerAmendment || totalAdded >= MaxFragmentsTotal) break;
                    // Zaostrzone dopasowanie: fragment musi ZMIENIAĆ artykuł (język diffu), nie tylko
                    // go wzmiankować — patrz AmendmentDiffMatcher (mechanizm „atraktora" z raportu).
                    if (!articles.Any(a => AmendmentDiffMatcher.MentionsArticleChange(ch.Text, a))) continue;
                    if (!seen.Add(ch.Id)) continue;
                    tagged.Add(ToAmendmentChunk(ch, am));
                    perAmendment++;
                    totalAdded++;
                }
            }
        }
        return tagged;
    }

    /// <summary>Ile fragmentów jednej noweli może dołożyć augmentacja (nowela zmieniająca jeden artykuł
    /// to zwykle 1-2 chunki diffu).</summary>
    private const int MaxFragmentsPerAmendment = 2;

    /// <summary>Twardy sufit dołożeń łącznie — augmentacja dokłada POZA TopK, więc bez sufitu rozsadza
    /// budżet promptu i grzebie trafną normę wśród fragmentów nowel.</summary>
    private const int MaxFragmentsTotal = 4;

    /// <summary>
    /// Słownik ExternalId→EffectiveDate wszystkich niewchłoniętych nowel w korpusie.
    ///
    /// Filtr `JsonExists` (operator jsonb `?`) zawęża skan do aktów, które FAKTYCZNIE mają klucz
    /// `unabsorbedAmendments` — a to garstka. Wcześniej zapytanie ściągało `TypedMetadata` KAŻDEGO aktu
    /// przy każdej turze czatu, która zwróciła choć jeden chunk aktu; komentarz w klasie mówił wprost, że
    /// jest to „tanie przy dzisiejszej skali korpusu ~40 aktów" i „przy pełnym korpusie wymagałoby
    /// indeksu/cache". Pełny ISAP jest już zembedowany, więc ten dług stał się aktywny — metadane aktów
    /// (słowa kluczowe, podstawy prawne, nowele) to duże jsonb i przesyłaliśmy je wszystkie, żeby
    /// zwykle nie znaleźć nic.
    ///
    /// Co ZOSTAJE do zrobienia i dlaczego nie tutaj: to nadal skan tabeli `documents` (brak indeksu na
    /// `DocType`, brak GIN na `TypedMetadata`). Cache procesowy albo indeks częściowy zdjąłby resztę, ale
    /// jedno wymaga unieważniania po ingeście, a drugie migracji na 523k dokumentów — do zrobienia
    /// z pomiarem na pełnym korpusie, nie na wyczucie.
    /// </summary>
    private async Task<Dictionary<string, string?>> BuildUnabsorbedDatesAsync(CancellationToken ct)
    {
        var metas = await db.Documents
            .Where(d => d.DocType == DocTypes.Act && d.TypedMetadata != null
                        && EF.Functions.JsonExists(d.TypedMetadata, "unabsorbedAmendments"))
            .Select(d => d.TypedMetadata).ToListAsync(ct);
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in metas)
            foreach (var am in ParseUnabsorbed(meta))
                map[am.EliId] = am.EffectiveDate;
        return map;
    }

    /// <summary>Wiersz chunka noweli W PROJEKCJI — bez `Embedding` i `SearchVector` (tu nieużywanych).</summary>
    /// <summary>
    /// Buduje marker noweli — KLUCZOWE: rozstrzyga w KODZIE (nie zostawia LLM-owi), czy data wejścia
    /// w życie już minęła. Diagnoza 2026-08-22 (pytanie o zastępstwo aplikanta adwokackiego): model bez
    /// tej informacji dostawał samą datę ("obowiązuje od 2026-06-18") bez punktu odniesienia „dziś" —
    /// nie ma jak wywnioskować, że ta data już minęła, więc zgadywał (i zgadł źle: opisał JUŻ
    /// obowiązującą zmianę jako przyszłą, „od 18 czerwca zasady się zmienią"). LLM nie zna dzisiejszej
    /// daty z kontekstu treningu — musi dostać gotowy werdykt, nie surowe dane do policzenia samemu.
    /// </summary>
    private static string BuildMarker(string? effectiveDateRaw)
    {
        if (string.IsNullOrWhiteSpace(effectiveDateRaw))
            return "[NOWELIZACJA — data wejścia w życie nieznana, jeszcze niewchłonięta do tekstu jednolitego]\n";

        if (!DateOnly.TryParse(effectiveDateRaw, out var effectiveDate))
            // Data w metadanych nieparsowalna — nie zgaduj status, zostaw neutralny opis jak dotąd
            // (bez rozstrzygnięcia JUŻ/JESZCZE NIE), reguła 6 promptu i tak każe zestawić oba źródła.
            return $"[NOWELIZACJA — obowiązuje od {effectiveDateRaw}, jeszcze niewchłonięta do tekstu jednolitego]\n";

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var status = effectiveDate <= today
            ? $"JUŻ OBOWIĄZUJE od {effectiveDateRaw} — to jest AKTUALNY, dziś obowiązujący stan prawny"
            : $"WEJDZIE W ŻYCIE {effectiveDateRaw} — jeszcze NIE obowiązuje, do tej daty obowiązuje tekst jednolity";
        return $"[NOWELIZACJA — {status}, jeszcze niewchłonięta do tekstu jednolitego]\n";
    }

    private sealed record AmendmentChunkRow(
        Guid Id, Guid DocumentId, string Text, string? Section, JsonDocument? Locator,
        string Source, string DocType, string Title, string? SourceUrl);

    private static List<AmendmentRef> ParseUnabsorbed(JsonDocument? meta)
    {
        if (meta is null
            || !meta.RootElement.TryGetProperty("unabsorbedAmendments", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];
        try { return arr.Deserialize<List<AmendmentRef>>() ?? []; }
        catch { return []; }
    }

    private static RetrievedChunk ToAmendmentChunk(AmendmentChunkRow ch, AmendmentRef am)
    {
        var marker = BuildMarker(am.EffectiveDate);
        return new RetrievedChunk
        {
            ChunkId = ch.Id,
            DocumentId = ch.DocumentId,
            Text = marker + ch.Text,
            Section = ch.Section,
            Source = ch.Source,
            DocType = ch.DocType,
            Title = ch.Title,
            SourceUrl = ch.SourceUrl,
            Locator = ch.Locator is null ? null : ch.Locator.Deserialize<CitationLocator>(),
            Score = double.MaxValue, // świeża nowela — prominentnie
            Similarity = null,
            AmendmentEffectiveDate = am.EffectiveDate, // AKT-4: pod chip w UI, niezależnie od markera w Text
        };
    }
}
