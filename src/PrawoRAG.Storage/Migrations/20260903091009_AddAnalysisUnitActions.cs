using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisUnitActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Suggestion",
                table: "analysis_units",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Violates",
                table: "analysis_units",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Suggestion",
                table: "analysis_units");

            migrationBuilder.DropColumn(
                name: "Violates",
                table: "analysis_units");
        }
    }
}
