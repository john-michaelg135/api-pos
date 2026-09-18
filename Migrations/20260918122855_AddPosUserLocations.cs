using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddPosUserLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PosUserLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AssignedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosUserLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosUserLocations_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "LocationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PosUserLocations_AuthUserId",
                table: "PosUserLocations",
                column: "AuthUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PosUserLocations_AuthUserId_LocationId",
                table: "PosUserLocations",
                columns: new[] { "AuthUserId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosUserLocations_LocationId",
                table: "PosUserLocations",
                column: "LocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PosUserLocations");
        }
    }
}
