using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddSeniorPwdOrderDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdBarangay",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdCity",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdId",
                table: "Orders",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdName",
                table: "Orders",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdProvince",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdStreet",
                table: "Orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeniorPwdZipCode",
                table: "Orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeniorPwdBarangay",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdCity",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdProvince",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdStreet",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SeniorPwdZipCode",
                table: "Orders");
        }
    }
}
