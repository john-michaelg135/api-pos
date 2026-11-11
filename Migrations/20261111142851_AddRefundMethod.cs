using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundMethod",
                table: "RefundRequests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Existing refunds: the way the order was paid (old COD orders were settled in cash).
            migrationBuilder.Sql("""
                UPDATE "RefundRequests" r
                SET "RefundMethod" = CASE
                    WHEN LOWER(TRIM(o."PaymentMethod")) IN ('cash', 'cod', 'cash on delivery') THEN 'Cash'
                    ELSE 'GCash'
                END
                FROM "Orders" o
                WHERE o."OrderId" = r."OrderId" AND r."RefundMethod" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefundMethod",
                table: "RefundRequests");
        }
    }
}
