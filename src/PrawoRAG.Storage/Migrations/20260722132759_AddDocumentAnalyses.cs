using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAnalyses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UnitsTotal = table.Column<int>(type: "integer", nullable: false),
                    UnitsTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "analysis_units",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitIndex = table.Column<int>(type: "integer", nullable: false),
                    Heading = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: true),
                    Sources = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    CitationClean = table.Column<bool>(type: "boolean", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_units", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analysis_units_analyses_AnalysisId",
                        column: x => x.AnalysisId,
                        principalTable: "analyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "analysis_unit_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_unit_feedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analysis_unit_feedback_analysis_units_AnalysisUnitId",
                        column: x => x.AnalysisUnitId,
                        principalTable: "analysis_units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analyses_UserId_UpdatedAt",
                table: "analyses",
                columns: new[] { "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analysis_unit_feedback_AnalysisUnitId",
                table: "analysis_unit_feedback",
                column: "AnalysisUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_analysis_units_AnalysisId_UnitIndex",
                table: "analysis_units",
                columns: new[] { "AnalysisId", "UnitIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_unit_feedback");

            migrationBuilder.DropTable(
                name: "analysis_units");

            migrationBuilder.DropTable(
                name: "analyses");
        }
    }
}
