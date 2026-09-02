using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisInterruptReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterruptReason",
                table: "analyses",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterruptReason",
                table: "analyses");
        }
    }
}
