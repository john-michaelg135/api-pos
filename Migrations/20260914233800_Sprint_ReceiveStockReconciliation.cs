using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class Sprint_ReceiveStockReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciledAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationState",
                table: "StockTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unreconciled");

            migrationBuilder.AddColumn<string>(
                name: "StatusSyncState",
                table: "StockTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusSyncedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DamagedQuantity",
                table: "StockTransferItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReceivedQuantity",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolvedVariationId",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sku",
                table: "StockTransferItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferId",
                table: "StockReceivings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sku",
                table: "ProductVariations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReceivings_TransferId",
                table: "StockReceivings",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariations_Sku",
                table: "ProductVariations",
                column: "Sku",
                unique: true,
                filter: "\"Sku\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockReceivings_TransferId",
                table: "StockReceivings");

            migrationBuilder.DropIndex(
                name: "IX_ProductVariations_Sku",
                table: "ProductVariations");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReconciliationState",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "StatusSyncState",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "StatusSyncedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "DamagedQuantity",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "ResolvedVariationId",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "Sku",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "TransferId",
                table: "StockReceivings");

            migrationBuilder.DropColumn(
                name: "Sku",
                table: "ProductVariations");
        }
    }
}
