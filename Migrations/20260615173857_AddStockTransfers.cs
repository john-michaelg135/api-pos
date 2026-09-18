using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddStockTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"Vouchers\";");
            migrationBuilder.Sql("ALTER TABLE \"Orders\" DROP COLUMN IF EXISTS \"AppliedVoucherCode\";");
            migrationBuilder.Sql("ALTER TABLE \"Orders\" DROP COLUMN IF EXISTS \"VoucherDiscountAmount\";");

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    TransferId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceLocationId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceLocationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DestinationBranchId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DestinationBranchName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.TransferId);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferId = table.Column<string>(type: "character varying(50)", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProductName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockTransferItems_StockTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "TransferId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferItems_TransferId",
                table: "StockTransferItems",
                column: "TransferId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockTransferItems");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.AddColumn<string>(
                name: "AppliedVoucherCode",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VoucherDiscountAmount",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Vouchers",
                columns: table => new
                {
                    VoucherId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    DiscountType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    MinimumSpend = table.Column<decimal>(type: "numeric(12,2)", nullable: false, defaultValue: 0m),
                    VoucherCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vouchers", x => x.VoucherId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vouchers_VoucherCode",
                table: "Vouchers",
                column: "VoucherCode",
                unique: true);
        }
    }
}
