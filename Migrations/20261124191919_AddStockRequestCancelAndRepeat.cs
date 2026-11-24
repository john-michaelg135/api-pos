using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddStockRequestCancelAndRepeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockRequests_OneOpenPerVariationBranch",
                table: "StockRequests");

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "StockRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "StockRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledBy",
                table: "StockRequests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByName",
                table: "StockRequests",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientRequestId",
                table: "StockRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RefusedOnSubmit",
                table: "StockRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShippedAt",
                table: "StockRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockRequests_ClientRequestId",
                table: "StockRequests",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockRequests_VariationId_LocationId",
                table: "StockRequests",
                columns: new[] { "VariationId", "LocationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockRequests_ClientRequestId",
                table: "StockRequests");

            migrationBuilder.DropIndex(
                name: "IX_StockRequests_VariationId_LocationId",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "CancelledByName",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "RefusedOnSubmit",
                table: "StockRequests");

            migrationBuilder.DropColumn(
                name: "ShippedAt",
                table: "StockRequests");

            migrationBuilder.CreateIndex(
                name: "IX_StockRequests_OneOpenPerVariationBranch",
                table: "StockRequests",
                columns: new[] { "VariationId", "LocationId" },
                unique: true,
                filter: "\"Status\" IN ('Sending', 'Submitted', 'Approved', 'In Transit')");
        }
    }
}
