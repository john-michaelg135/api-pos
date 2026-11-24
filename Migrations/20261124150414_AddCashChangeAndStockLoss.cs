using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddCashChangeAndStockLoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LossReason",
                table: "StockAdjustments",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "StockAdjustments",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashTenderedAmount",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChangeGivenAmount",
                table: "StartingCashEntries",
                type: "numeric(12,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LossReason",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "CashTenderedAmount",
                table: "StartingCashEntries");

            migrationBuilder.DropColumn(
                name: "ChangeGivenAmount",
                table: "StartingCashEntries");
        }
    }
}
