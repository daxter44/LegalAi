using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPlansAndUsageCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BillingAnchorUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanId",
                table: "AspNetUsers",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlanStatus",
                table: "AspNetUsers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanValidUntilUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "usage_counters",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_counters", x => new { x.Scope, x.Key, x.PeriodStart });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_counters");

            migrationBuilder.DropColumn(
                name: "BillingAnchorUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PlanStatus",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PlanValidUntilUtc",
                table: "AspNetUsers");
        }
    }
}
