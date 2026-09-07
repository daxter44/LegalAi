using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCaseNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseNumber",
                table: "documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Backfill z istniejącego korpusu (SAOS + NSA) BEZ re-embeddingu: znormalizowana sygnatura
            // z TypedMetadata->>'caseNumber'. Normalizacja MUSI odpowiadać CaseNumberKey.Normalize:
            // trim + kolaps białych znaków do pojedynczej spacji + wielkie litery. Dzięki temu dokładne
            // wyszukiwanie po sygnaturze działa od razu na obecnych orzeczeniach, nie tylko na nowych.
            migrationBuilder.Sql("""
                UPDATE documents
                SET "CaseNumber" = upper(btrim(regexp_replace("TypedMetadata"->>'caseNumber', '\s+', ' ', 'g')))
                WHERE nullif(btrim("TypedMetadata"->>'caseNumber'), '') IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_documents_CaseNumber",
                table: "documents",
                column: "CaseNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_CaseNumber",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "CaseNumber",
                table: "documents");
        }
    }
}
