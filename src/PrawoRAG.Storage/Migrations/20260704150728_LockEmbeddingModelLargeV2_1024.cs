using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LockEmbeddingModelLargeV2_1024 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stare wektory (768-wym., inny model) są niekompatybilne z nowym wymiarem —
            // czyścimy je; pipeline ingestii wykryje EmbeddedWith != nowy model i przeliczy je od nowa.
            migrationBuilder.Sql("UPDATE chunks SET \"Embedding\" = NULL, \"EmbeddedWith\" = NULL;");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "chunks",
                type: "vector(1024)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(768)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "chunks",
                type: "vector(768)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)",
                oldNullable: true);
        }
    }
}
