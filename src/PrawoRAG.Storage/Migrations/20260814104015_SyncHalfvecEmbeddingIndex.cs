using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <summary>
    /// Doprowadza `IX_chunks_Embedding` do postaci, której FAKTYCZNIE używa tor gęsty: indeks HNSW
    /// WYRAŻENIOWY na `("Embedding"::halfvec(1024)) halfvec_cosine_ops`.
    ///
    /// Dlaczego: `HybridRetriever.DenseAsync` rzutuje obie strony `<=>` na `halfvec(1024)` (fp16 —
    /// oszczędność pamięci przy budowie grafu, patrz SESJA-2026-07-17), a indeks fp32
    /// `("Embedding" vector_cosine_ops)` tworzony przez `InitialSchema` takiego wyrażenia NIE OBSŁUGUJE.
    /// Zmierzone planem zapytania przy `enable_seqscan = off` (czyli to nie preferencja plannera, a brak
    /// możliwości dopasowania indeksu): fp32 → `Sort + Seq Scan`, wyrażeniowy halfvec → `Index Scan`.
    /// Skutek przed tą migracją: każde środowisko postawione z samych migracji (nowa maszyna, odtworzenie
    /// po awarii, CI na pełnym dumpie) robiło sequential scan po 7,4 mln wierszy przy KAŻDYM pytaniu —
    /// bez żadnego sygnału błędu, tylko „wolno". Żywa baza produkcyjna miała właściwy indeks WYŁĄCZNIE
    /// dlatego, że zbudowano go ręcznie (12,5 h, 18 GB).
    ///
    /// IDEMPOTENCJA I KOSZT — powód, dla którego to jest DO-block, a nie zwykły drop+create:
    /// na bazie, która MA już wariant halfvec pod tą nazwą (produkcja), migracja jest NO-OPEM. Bez tego
    /// warunku `dotnet ef database update` skasowałby 18 GB indeksu i budował go od nowa ~12,5 h.
    /// Na bazie z wariantem fp32 (świeże środowisko, dev) indeks jest podmieniany — przy pustej/małej
    /// tabeli to natychmiastowe. UWAGA: gdyby ta migracja trafiła na DUŻĄ bazę z indeksem fp32,
    /// przebudowa potrwa godziny i zajmie ~18 GB — wtedy lepiej zrobić to runbookiem
    /// (`maintenance_work_mem`, `max_parallel_maintenance_workers=0`), a migrację uznać za już wykonaną.
    /// </summary>
    /// <inheritdoc />
    public partial class SyncHalfvecEmbeddingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $do$
                DECLARE
                    existing_def text;
                BEGIN
                    SELECT indexdef INTO existing_def
                    FROM pg_indexes
                    WHERE schemaname = current_schema()
                      AND tablename = 'chunks'
                      AND indexname = 'IX_chunks_Embedding';

                    IF existing_def LIKE '%halfvec%' THEN
                        RAISE NOTICE 'IX_chunks_Embedding jest juz indeksem wyrazeniowym halfvec - pomijam (zero przebudowy).';
                        RETURN;
                    END IF;

                    IF existing_def IS NOT NULL THEN
                        RAISE NOTICE 'Usuwam IX_chunks_Embedding w postaci fp32 - nieuzywalny dla zapytania z rzutem na halfvec.';
                        EXECUTE 'DROP INDEX "IX_chunks_Embedding"';
                    END IF;

                    RAISE NOTICE 'Tworze IX_chunks_Embedding jako indeks wyrazeniowy halfvec.';
                    EXECUTE 'CREATE INDEX "IX_chunks_Embedding" ON chunks USING hnsw (("Embedding"::halfvec(1024)) halfvec_cosine_ops)';
                END
                $do$;
                """);
        }

        /// <summary>
        /// Powrót do wariantu fp32 z `InitialSchema`. UWAGA: na pełnym korpusie ten indeks jest
        /// NIEBUDOWALNY w środowisku 3060/WSL2 — policzono ~33 GB grafu przy realnym sufcie ~15 GB RAM
        /// (SESJA-2026-07-17, sekcja 1). Na dużej bazie tego `Down` po prostu nie odpalać.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_chunks_Embedding\";");
            migrationBuilder.CreateIndex(
                name: "IX_chunks_Embedding",
                table: "chunks",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }
    }
}
