using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddStartingCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StartingCashEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CashierName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartingCashEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StartingCashEntries_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StartingCashEntries_AuthUserId_CashierName_LocationId_Busin~",
                table: "StartingCashEntries",
                columns: new[] { "AuthUserId", "CashierName", "LocationId", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StartingCashEntries_BusinessDate",
                table: "StartingCashEntries",
                column: "BusinessDate");

            migrationBuilder.CreateIndex(
                name: "IX_StartingCashEntries_LocationId",
                table: "StartingCashEntries",
                column: "LocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StartingCashEntries");
        }
    }
}
