using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api_pos.Migrations
{
    /// <summary>
    /// Older orders were marked Paid before payment rows were recorded, so the dashboard and the
    /// end-of-shift count each ran an extra "paid orders without payments" query to include them.
    /// This gives each such order the payment row it should have had: the full total, on the day the
    /// order was placed, through the order's payment method. Both totals stay exactly the same, and
    /// the dashboard no longer needs the extra query.
    ///
    /// GatewayReferenceNumber stays NULL on purpose: Order Management treats any payment with a
    /// reference as an online (Xendit) payment link, so tagging these rows would show old cash
    /// orders as "Cashless Payment Confirmed". The rows therefore look like ordinary payments,
    /// and Down cannot tell them apart; it leaves them in place (the old fallback ignores them).
    /// </summary>
    public partial class BackfillLegacyPaidOrderPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "Payments" ("OrderId", "AmountPaid", "PaymentChannel", "PaymentStatus", "PaidAt")
                SELECT o."OrderId", o."TotalAmount", o."PaymentMethod", 'Success', o."CreatedAt"
                FROM "Orders" AS o
                WHERE o."PaymentStatus" = 'Paid'
                  AND o."OrderStatus" NOT IN ('Voided', 'Cancelled')
                  AND NOT EXISTS (
                      SELECT 1 FROM "Payments" AS p
                      WHERE p."OrderId" = o."OrderId"
                        AND p."PaymentStatus" IN ('Success', 'Paid')
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op: the backfilled rows are indistinguishable from real payments.
            // Rolling back the code is still safe, because the old fallback only counts Paid orders
            // that have no payment row, so nothing is counted twice.
        }
    }
}
