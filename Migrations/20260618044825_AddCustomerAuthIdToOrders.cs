using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAuthIdToOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerAuthId",
                table: "Orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerAuthId",
                table: "Orders");
        }
    }
}
