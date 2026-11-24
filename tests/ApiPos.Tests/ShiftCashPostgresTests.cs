using Applications.Services;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ApiPos.Tests;

/// <summary>
/// The Money Audit's drawer totals run as SQL, so they are checked against real Postgres.
/// Set POS_TEST_POSTGRES (e.g. <c>Host=localhost;Port=5432;Username=postgres;Password=...</c>);
/// without it these tests return immediately.
/// </summary>
public class ShiftCashPostgresTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("POS_TEST_POSTGRES");

    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name = $"pos_cash_test_{Guid.NewGuid():N}";
        public string ConnectionString { get; }

        public Scratch(string server)
        {
            _admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
            ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = _name, Pooling = false }.ConnectionString;
        }

        public PosDbContext Context() => new(new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(ConnectionString).Options);

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static int _orderId = 100;

    private static void Sale(PosDbContext db, DateTime at, string method, decimal total, decimal? tendered, string status = "Completed")
    {
        var id = ++_orderId;
        db.Orders.Add(new Order
        {
            OrderId = id, OrderNumber = $"ORD-T-{id}", OrderType = "Walk-in", OrderSource = "POS", LocationId = 8,
            PaymentMethod = method, PaymentStatus = "Paid", OrderStatus = status, TotalAmount = total,
            AmountTendered = tendered, ChangeAmount = tendered - total, CreatedAt = at, UpdatedAt = at,
        });
        db.Payments.Add(new Payment { OrderId = id, AmountPaid = total, PaymentChannel = method, PaymentStatus = "Success", PaidAt = at });
    }

    [Fact]
    public async Task Totals_track_tendered_cash_and_change_for_open_and_closed_shifts()
    {
        if (string.IsNullOrWhiteSpace(Server)) return;
        await using var scratch = new Scratch(Server);
        await using (var setup = scratch.Context()) await setup.Database.MigrateAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var dayStart = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);
        var opened = dayStart.AddMinutes(5);

        await using (var db = scratch.Context())
        {
            db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
            db.StartingCashEntries.Add(new StartingCash
            {
                Id = 1, AuthUserId = "portal-cashier-7", CashierName = "Ana Cruz", LocationId = 8,
                BusinessDate = today, Amount = 500m, CreatedAt = opened,
            });
            Sale(db, opened.AddMinutes(1), "Cash", 450m, 500m);     // ₱50 change
            Sale(db, opened.AddMinutes(2), "Cash", 120m, 1000m);    // ₱880 change
            Sale(db, opened.AddMinutes(3), "Cash", 75m, null);      // exact, no tender recorded
            Sale(db, opened.AddMinutes(4), "GCash", 300m, null);    // wallet, not drawer
            Sale(db, opened.AddMinutes(5), "Cash", 999m, 1000m, status: "Voided"); // never counted
            await db.SaveChangesAsync();
        }

        await using (var db = scratch.Context())
        {
            var open = (await ShiftCashCalculator.CalculateAsync(db, [1], DateTime.UtcNow))[1];
            Assert.Equal(645m, open.CashSales);
            Assert.Equal(930m, open.ChangeGiven);
            Assert.Equal(1575m, open.CashTendered);
            Assert.Equal(300m, open.NonCashSales);
            Assert.Equal(0m, open.CashRefunds);
            Assert.Equal(500m + 1575m - 930m, open.ExpectedCash(500m));
        }

        // Closing the shift before the third sale leaves it (and later ones) out.
        await using (var db = scratch.Context())
        {
            await db.StartingCashEntries.Where(e => e.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.EndShiftAt, opened.AddMinutes(2).AddSeconds(30)));
            var closed = (await ShiftCashCalculator.CalculateAsync(db, [1], DateTime.UtcNow))[1];
            Assert.Equal(570m, closed.CashSales);
            Assert.Equal(930m, closed.ChangeGiven);
            Assert.Equal(1500m, closed.CashTendered);
            Assert.Equal(0m, closed.NonCashSales);
        }
    }
}
