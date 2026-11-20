using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LowStockAlertOpen",
                table: "Stocks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LowStockEpisode",
                table: "Stocks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Rollout baseline: shortages that exist today count as an episode already alerted, so
            // the first stock write after deploy doesn't flood the bell. A recovery and a new drop
            // after this point raise an alert as usual.
            migrationBuilder.Sql(
                "UPDATE \"Stocks\" SET \"LowStockAlertOpen\" = TRUE " +
                "WHERE \"MinThreshold\" > 0 AND \"Quantity\" < \"MinThreshold\";");

            migrationBuilder.CreateTable(
                name: "StaffAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurrenceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Module = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    OrderId = table.Column<int>(type: "integer", nullable: true),
                    TransferId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    VariationId = table.Column<int>(type: "integer", nullable: true),
                    SourceHistoryId = table.Column<int>(type: "integer", nullable: true),
                    RecipientPortalUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequesterPortalUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Partial = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LinkPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadByPortalUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AudienceResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NudgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffAlertCopies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertId = table.Column<long>(type: "bigint", nullable: false),
                    PortalUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RemoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAlertCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffAlertCopies_StaffAlerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "StaffAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlertCopies_AlertId_PortalUserId",
                table: "StaffAlertCopies",
                columns: new[] { "AlertId", "PortalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlertCopies_Done_NextAttemptAt",
                table: "StaffAlertCopies",
                columns: new[] { "Done", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_AudienceResolvedAt",
                table: "StaffAlerts",
                column: "AudienceResolvedAt",
                filter: "\"AudienceResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_LocationId_Id",
                table: "StaffAlerts",
                columns: new[] { "LocationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_NudgedAt",
                table: "StaffAlerts",
                column: "NudgedAt",
                filter: "\"NudgedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_OccurrenceKey",
                table: "StaffAlerts",
                column: "OccurrenceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_RecipientPortalUserId",
                table: "StaffAlerts",
                column: "RecipientPortalUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffAlerts_SourceHistoryId",
                table: "StaffAlerts",
                column: "SourceHistoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffAlertCopies");

            migrationBuilder.DropTable(
                name: "StaffAlerts");

            migrationBuilder.DropColumn(
                name: "LowStockAlertOpen",
                table: "Stocks");

            migrationBuilder.DropColumn(
                name: "LowStockEpisode",
                table: "Stocks");
        }
    }
}
