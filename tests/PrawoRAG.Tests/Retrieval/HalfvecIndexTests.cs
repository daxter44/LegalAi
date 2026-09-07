using Microsoft.EntityFrameworkCore;
using PrawoRAG.Storage;

namespace PrawoRAG.Tests.Retrieval;

/// <summary>
/// T-IDX — schemat bazy MUSI mieć indeks HNSW w postaci, której używa tor gęsty.
///
/// Blokuje regresję, która przez ~4 tygodnie żyła niezauważona: `HybridRetriever.DenseAsync` rzutuje
/// obie strony `<=>` na `halfvec(1024)`, a migracje tworzyły indeks fp32 (`vector_cosine_ops`), którego
/// takie wyrażenie NIE MOŻE użyć. Zmierzone planem zapytania przy `enable_seqscan = off` (czyli brak
/// dopasowania indeksu, nie preferencja plannera): fp32 → `Sort + Seq Scan`, wyrażeniowy halfvec →
/// `Index Scan`. Na pełnym korpusie (7,4 mln chunków) to różnica między indeksowym wyszukiwaniem
/// i sequential scanem przy KAŻDYM pytaniu — bez żadnego sygnału błędu, tylko „wolno". Żywa produkcja
/// miała właściwy indeks wyłącznie dzięki ręcznej budowie (12,5 h), więc awaria była niewidoczna
/// dopóki ktoś nie postawił środowiska z samych migracji.
///
/// Test celowo NIE mierzy planu zapytania: wymagałby korpusu na tyle dużego, żeby planner wybrał
/// indeks, a to nie jest test jednostkowy. Sprawdzamy niezmiennik, który da się sprawdzić tanio:
/// definicja indeksu zawiera `halfvec` — czyli zgadza się z rzutem w `DenseAsync`.
/// </summary>
[Collection("LiveDb")]
public class HalfvecIndexTests
{
    private static readonly string Conn =
        Environment.GetEnvironmentVariable("PRAWORAG_DB")
        ?? "Host=localhost;Port=5432;Database=praworag;Username=praworag;Password=praworag";

    private static PrawoRagDbContext NewDb() =>
        new(new DbContextOptionsBuilder<PrawoRagDbContext>().UseNpgsql(Conn, o => o.UseVector()).Options);

    [Fact] // IDX1: indeks toru gęstego jest wyrażeniowy na halfvec, a nie fp32
    public async Task Dense_lane_index_is_expression_on_halfvec()
    {
        await using var db = NewDb();
        var definition = await IndexDefinitionAsync(db, "IX_chunks_Embedding");

        Assert.NotNull(definition); // brak indeksu = seq scan po całej tabeli przy każdym pytaniu
        Assert.Contains("halfvec", definition);
        Assert.Contains("hnsw", definition);
        // Wariant fp32 nie może wrócić: `vector_cosine_ops` pojawia się TYLKO w postaci fp32
        // (wyrażeniowy halfvec używa `halfvec_cosine_ops`).
        Assert.DoesNotContain("vector_cosine_ops", definition.Replace("halfvec_cosine_ops", ""));
    }

    [Fact] // IDX2: tor rzadki (BM25) ma swój indeks GIN — ten sam klasa problemu, tańsza weryfikacja
    public async Task Sparse_lane_has_gin_index()
    {
        await using var db = NewDb();
        var definition = await IndexDefinitionAsync(db, "IX_chunks_SearchVector");

        Assert.NotNull(definition);
        Assert.Contains("gin", definition);
    }

    private static async Task<string?> IndexDefinitionAsync(PrawoRagDbContext db, string indexName)
    {
        var rows = await db.Database
            .SqlQueryRaw<string?>(
                """
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE schemaname = current_schema() AND tablename = 'chunks' AND indexname = {0}
                """,
                indexName)
            .ToListAsync();
        return rows.FirstOrDefault();
    }
}
