using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkArticleNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArticleNo",
                table: "chunks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chunks_ArticleNo",
                table: "chunks",
                column: "ArticleNo");

            // pg_trgm — rozmyte dopasowanie wskazówki aktu do tytułu (QU-2, odporność na polską odmianę).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // Backfill istniejących chunków: numer artykułu z lokatora jsonb (klucz „Article", PascalCase).
            migrationBuilder.Sql(
                "UPDATE chunks SET \"ArticleNo\" = LEFT(\"Locator\"->>'Article', 16) " +
                "WHERE \"Locator\" IS NOT NULL AND \"Locator\"->>'Article' IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chunks_ArticleNo",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "ArticleNo",
                table: "chunks");
        }
    }
}
