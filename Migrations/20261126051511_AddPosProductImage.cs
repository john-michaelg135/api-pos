using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddPosProductImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PosImage",
                table: "Products",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PosImage",
                table: "Products");
        }
    }
}
