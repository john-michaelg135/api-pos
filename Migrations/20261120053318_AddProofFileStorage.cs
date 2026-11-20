using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddProofFileStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ProofImage",
                table: "OrderRefundProofs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "ProofFileId",
                table: "OrderRefundProofs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProofFileId",
                table: "OrderDeliveryConfirmations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProofFileTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FileExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProofFileTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProofFileTasks_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderRefundProofs_ProofFileId",
                table: "OrderRefundProofs",
                column: "ProofFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderDeliveryConfirmations_ProofFileId",
                table: "OrderDeliveryConfirmations",
                column: "ProofFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProofFileTasks_FileId",
                table: "ProofFileTasks",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProofFileTasks_OrderId_Kind",
                table: "ProofFileTasks",
                columns: new[] { "OrderId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ProofFileTasks_Stage_NextAttemptAt",
                table: "ProofFileTasks",
                columns: new[] { "Stage", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows that only hold a File ID have no legacy photo; the old schema needs a value.
            migrationBuilder.Sql("UPDATE \"OrderRefundProofs\" SET \"ProofImage\" = '' WHERE \"ProofImage\" IS NULL;");

            migrationBuilder.DropTable(
                name: "ProofFileTasks");

            migrationBuilder.DropIndex(
                name: "IX_OrderRefundProofs_ProofFileId",
                table: "OrderRefundProofs");

            migrationBuilder.DropIndex(
                name: "IX_OrderDeliveryConfirmations_ProofFileId",
                table: "OrderDeliveryConfirmations");

            migrationBuilder.DropColumn(
                name: "ProofFileId",
                table: "OrderRefundProofs");

            migrationBuilder.DropColumn(
                name: "ProofFileId",
                table: "OrderDeliveryConfirmations");

            migrationBuilder.AlterColumn<string>(
                name: "ProofImage",
                table: "OrderRefundProofs",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
