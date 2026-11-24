using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Applications.Services;

/// <summary>
/// The money that went through one shift's cash drawer.
/// <para>
/// <see cref="CashTendered"/> is what customers handed over for cash sales and <see cref="ChangeGiven"/>
/// is what the cashier handed back, so <c>CashTendered - ChangeGiven</c> is <see cref="CashSales"/>.
/// The drawer should then hold <c>starting cash + CashTendered - ChangeGiven - CashRefunds</c>.
/// </para>
/// </summary>
public readonly record struct ShiftCashTotals(
    decimal CashSales,
    decimal CashRefunds,
    decimal NonCashSales,
    decimal CashTendered,
    decimal ChangeGiven)
{
    public static readonly ShiftCashTotals Zero = new(0, 0, 0, 0, 0);

    public decimal ExpectedCash(decimal startingCash) => startingCash + CashTendered - ChangeGiven - CashRefunds;
}

/// <summary>
/// Works out <see cref="ShiftCashTotals"/> for starting-cash entries. A closed shift is counted up to
/// its end; a shift still open is counted up to <c>asOf</c>, so the Money Audit can show the running
/// expected cash before the cashier counts the drawer.
/// <list type="bullet">
/// <item>Only cash goes into the drawer. GCash and other wallet payments are reported separately.</item>
/// <item>Change is the amount tendered less the amount paid, for cash payments with a recorded tender.
/// Sales recorded without a tender (older orders) count as paid exactly, with no change.</item>
/// <item>Refunds only come out of the drawer when the order was paid in cash.</item>
/// </list>
/// </summary>
public static class ShiftCashCalculator
{
    public static async Task<Dictionary<int, ShiftCashTotals>> CalculateAsync(
        PosDbContext db,
        IReadOnlyCollection<int> entryIds,
        DateTime asOf,
        CancellationToken cancellationToken = default)
    {
        var totals = new Dictionary<int, ShiftCashTotals>();
        if (entryIds.Count == 0) return totals;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var closeConnectionWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeConnectionWhenDone)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            command.CommandText = """
                WITH shift AS (
                    SELECT
                        e."Id",
                        e."LocationId",
                        (e."BusinessDate"::timestamp AT TIME ZONE 'Asia/Manila') AS "Start",
                        COALESCE(e."EndShiftAt", @asOf) AS "Cutoff"
                    FROM public."StartingCashEntries" e
                    WHERE e."Id" = ANY(@entryIds)
                ),
                received AS (
                    SELECT
                        s."Id" AS "EntryId",
                        COALESCE(LOWER(p."PaymentChannel"), '') = 'cash' AS "IsCash",
                        p."AmountPaid" AS "Amount",
                        CASE
                            WHEN COALESCE(LOWER(p."PaymentChannel"), '') = 'cash' AND o."AmountTendered" IS NOT NULL
                                THEN GREATEST(o."AmountTendered" - p."AmountPaid", 0)
                            ELSE 0
                        END AS "Change"
                    FROM shift s
                    JOIN public."Orders" o ON o."LocationId" = s."LocationId"
                    JOIN public."Payments" p ON p."OrderId" = o."OrderId"
                    WHERE p."PaymentStatus" IN ('Success', 'Paid')
                      AND p."PaidAt" >= s."Start"
                      AND p."PaidAt" < s."Cutoff"
                      AND o."OrderStatus" <> 'Voided'
                      -- A cancelled order's forfeited (non-refundable) downpayment is still money received.
                      AND (o."OrderStatus" <> 'Cancelled' OR o."PaymentStatus" = 'Forfeited')
                    UNION ALL
                    -- Paid orders from before payments were recorded separately.
                    SELECT
                        s."Id",
                        COALESCE(LOWER(o."PaymentMethod"), '') = 'cash',
                        o."TotalAmount",
                        CASE
                            WHEN COALESCE(LOWER(o."PaymentMethod"), '') = 'cash' AND o."AmountTendered" IS NOT NULL
                                THEN GREATEST(o."AmountTendered" - o."TotalAmount", 0)
                            ELSE 0
                        END
                    FROM shift s
                    JOIN public."Orders" o ON o."LocationId" = s."LocationId"
                    WHERE o."CreatedAt" >= s."Start"
                      AND o."CreatedAt" < s."Cutoff"
                      AND o."PaymentStatus" = 'Paid'
                      AND o."OrderStatus" NOT IN ('Voided', 'Cancelled')
                      AND NOT EXISTS (
                          SELECT 1
                          FROM public."Payments" p
                          WHERE p."OrderId" = o."OrderId"
                            AND p."PaymentStatus" IN ('Success', 'Paid')
                      )
                )
                SELECT
                    s."Id",
                    COALESCE((SELECT SUM(r."Amount") FROM received r WHERE r."EntryId" = s."Id" AND r."IsCash"), 0) AS "CashSales",
                    COALESCE((
                        SELECT SUM(item."UnitPrice" * refund."QuantityToReturn")
                        FROM public."RefundRequests" refund
                        JOIN public."OrderItems" item
                          ON item."OrderId" = refund."OrderId"
                         AND item."VariationId" = refund."VariationId"
                        JOIN public."Orders" o ON o."OrderId" = refund."OrderId"
                        WHERE refund."LocationId" = s."LocationId"
                          AND refund."Status" = 'Approved'
                          AND refund."ApprovedAt" >= s."Start"
                          AND refund."ApprovedAt" < s."Cutoff"
                          AND LOWER(o."PaymentMethod") = 'cash'
                    ), 0)
                    +
                    COALESCE((
                        SELECT SUM(o."TotalAmount")
                        FROM public."Orders" o
                        WHERE o."LocationId" = s."LocationId"
                          AND o."OrderStatus" = 'Refunded'
                          AND LOWER(o."PaymentMethod") = 'cash'
                          AND o."ApprovedAt" >= s."Start"
                          AND o."ApprovedAt" < s."Cutoff"
                          AND NOT EXISTS (
                              SELECT 1
                              FROM public."RefundRequests" refund
                              WHERE refund."OrderId" = o."OrderId"
                                AND refund."Status" = 'Approved'
                          )
                    ), 0) AS "CashRefunds",
                    COALESCE((SELECT SUM(r."Amount") FROM received r WHERE r."EntryId" = s."Id" AND NOT r."IsCash"), 0) AS "NonCashSales",
                    COALESCE((SELECT SUM(r."Change") FROM received r WHERE r."EntryId" = s."Id" AND r."IsCash"), 0) AS "ChangeGiven"
                FROM shift s
                """;
            command.Parameters.AddWithValue("entryIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, entryIds.ToArray());
            command.Parameters.AddWithValue("asOf", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(asOf, DateTimeKind.Utc));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var cashSales = reader.GetDecimal(1);
                var change = reader.GetDecimal(4);
                totals[reader.GetInt32(0)] = new ShiftCashTotals(
                    CashSales: cashSales,
                    CashRefunds: reader.GetDecimal(2),
                    NonCashSales: reader.GetDecimal(3),
                    CashTendered: cashSales + change,
                    ChangeGiven: change);
            }

            return totals;
        }
        finally
        {
            if (closeConnectionWhenDone)
                await connection.CloseAsync();
        }
    }
}
