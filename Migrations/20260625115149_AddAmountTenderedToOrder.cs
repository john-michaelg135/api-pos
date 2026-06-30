using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddAmountTenderedToOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountTendered",
                table: "Orders",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChangeAmount",
                table: "Orders",
                type: "numeric(12,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountTendered",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChangeAmount",
                table: "Orders");
        }
    }
}
