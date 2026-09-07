using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrawoRAG.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlanUpdatedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "processed_webhooks",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_webhooks", x => x.EventId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_webhooks");

            migrationBuilder.DropColumn(
                name: "PlanUpdatedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                table: "AspNetUsers");
        }
    }
}
