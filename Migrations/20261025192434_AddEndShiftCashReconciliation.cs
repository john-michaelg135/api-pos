using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddEndShiftCashReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EndShiftAmount",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndShiftAt",
                table: "StartingCashEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndShiftAuthUserId",
                table: "StartingCashEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndShiftCashierName",
                table: "StartingCashEntries",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SuccessfulSalesAmount",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedRefundAmount",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedCash",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndShiftAmount",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "EndShiftAt",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "EndShiftAuthUserId",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "EndShiftCashierName",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "SuccessfulSalesAmount",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "ApprovedRefundAmount",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "ExpectedCash",
                table: "StartingCashEntries");
        }
    }
}
