using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <summary>
    /// P6/AKT-2: indeks częściowy pod predykat, którym `TemporalAugmenter.BuildUnabsorbedDatesAsync`
    /// filtruje `documents` PRZY KAŻDEJ turze czatu zwracającej choć jeden chunk aktu
    /// (`DocType == "act" &amp;&amp; TypedMetadata != null &amp;&amp; EF.Functions.JsonExists(TypedMetadata,
    /// "unabsorbedAmendments")` — Npgsql tłumaczy `JsonExists` na operator jsonb <c>?</c>, stąd ten sam
    /// operator w filtrze indeksu, żeby planner rozpoznał implikację).
    ///
    /// Zmierzone 2026-08-24 (docs/PLAN-SIZING-DEPLOY-2026-08-24.md, „Odkrycie 2" — 16 równoległych
    /// `/api/chat` na pełnym korpusie): bez tego indeksu `augment` idzie z 700-867 ms solo do 18,5-22 s
    /// pod obciążeniem — sekwencyjny skan 533k wierszy `documents` (TypedMetadata to duże, czasem
    /// TOASTowane jsonb), nie problem CPU/RAM. Tabela jest mała (~1,5 GB) — budowa indeksu to sekundy,
    /// nie godziny jak przy HNSW (`SyncHalfvecEmbeddingIndex`), więc bez potrzeby na CONCURRENTLY/DO-block.
    /// </summary>
    /// <inheritdoc />
    public partial class AddDocumentsUnabsorbedAmendmentsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_documents_UnabsorbedAmendments",
                table: "documents",
                column: "Id",
                filter: "\"DocType\" = 'act' AND \"TypedMetadata\" IS NOT NULL AND \"TypedMetadata\" ? 'unabsorbedAmendments'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_UnabsorbedAmendments",
                table: "documents");
        }
    }
}
