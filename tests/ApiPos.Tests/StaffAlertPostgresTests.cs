using Applications.StaffAlerts;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ApiPos.Tests;

/// <summary>
/// The relational path (shared transaction, savepoint on a duplicate occurrence, rollout baseline)
/// against a real PostgreSQL. Opt-in: set POS_TEST_POSTGRES to a connection string for a server
/// where a throwaway database may be created, e.g.
/// <c>Host=localhost;Port=5432;Username=postgres;Password=...</c>. Each test creates and drops its own database.
/// Without the variable these tests return immediately.
/// </summary>
public class StaffAlertPostgresTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("POS_TEST_POSTGRES");

    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name = $"pos_alerts_test_{Guid.NewGuid():N}";
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

    private static async Task<Scratch?> MigratedAsync(string? target = null)
    {
        if (string.IsNullOrWhiteSpace(Server)) return null;
        var scratch = new Scratch(Server);
        await using var db = scratch.Context();
        await db.GetService<IMigrator>().MigrateAsync(target);
        return scratch;
    }

    private static async Task SeedCatalogAsync(PosDbContext db)
    {
        db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
        db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads" });
        db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UH-300|Jar|300g" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Concurrent_drops_below_minimum_raise_one_alert_and_both_writes_commit()
    {
        await using var scratch = await MigratedAsync();
        if (scratch is null) return;
        await using (var setup = scratch.Context())
        {
            await SeedCatalogAsync(setup);
            setup.Stocks.Add(new Stock { VariationId = 11, LocationId = 8, Quantity = 20, MinThreshold = 10, UpdatedAt = DateTime.UtcNow });
            await setup.SaveChangesAsync();
        }

        // Two requests read the row before either writes, so both see "not low" and claim episode 1.
        await using var first = scratch.Context();
        await using var second = scratch.Context();
        var a = await first.Stocks.SingleAsync();
        var b = await second.Stocks.SingleAsync();
        a.Quantity = 5;
        b.Quantity = 4;
        await first.SaveChangesAsync();
        await second.SaveChangesAsync(); // duplicate occurrence is skipped, the stock write still commits

        await using var check = scratch.Context();
        Assert.Equal("low-stock:8:11:1", (await check.StaffAlerts.SingleAsync()).OccurrenceKey);
        Assert.Equal(4, (await check.Stocks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task A_replayed_occurrence_is_skipped_and_a_failed_business_write_saves_no_alert()
    {
        await using var scratch = await MigratedAsync();
        if (scratch is null) return;
        await using (var setup = scratch.Context()) await SeedCatalogAsync(setup);

        StaffAlertIntent Arrival() => new()
        {
            Kind = StaffAlertKinds.TransferArrived,
            OccurrenceKey = StaffAlertIntent.TransferKey("trans_001"),
            LocationId = 8,
            TransferId = "trans_001",
        };

        await using (var db = scratch.Context())
        {
            db.RaiseStaffAlert(Arrival());
            db.StockTransfers.Add(new StockTransfer { TransferId = "trans_001", DestinationBranchId = "8", TransferDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var replay = scratch.Context())
        {
            replay.RaiseStaffAlert(Arrival());
            replay.Locations.Add(new Location { LocationId = 9, LocationName = "Pasig Branch", CreatedAt = DateTime.UtcNow });
            await replay.SaveChangesAsync();
        }
        await using (var failing = scratch.Context())
        {
            failing.RaiseStaffAlert(new StaffAlertIntent
            {
                Kind = StaffAlertKinds.TransferArrived, OccurrenceKey = StaffAlertIntent.TransferKey("trans_002"), LocationId = 8, TransferId = "trans_002",
            });
            failing.Locations.Add(new Location { LocationId = 8, LocationName = "Duplicate", CreatedAt = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => failing.SaveChangesAsync());
        }

        await using var check = scratch.Context();
        Assert.Equal("transfer-arrived:trans_001", (await check.StaffAlerts.SingleAsync()).OccurrenceKey);
        Assert.True(await check.Locations.AnyAsync(l => l.LocationId == 9)); // the replay's own write committed
    }

    [Fact]
    public async Task Rollout_baselines_existing_shortages()
    {
        await using var scratch = await MigratedAsync("20261120053318_AddProofFileStorage");
        if (scratch is null) return;
        await using (var setup = scratch.Context())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Locations" ("LocationId", "LocationName", "LocationType", "IsActive", "CreatedAt") VALUES (8, 'Makati Branch', 'Store', TRUE, now());
                INSERT INTO "Products" ("ProductId", "ProductName", "ProductCategory", "IsActive") VALUES (1, 'Ube Halaya', 'Spreads', TRUE);
                INSERT INTO "ProductVariations" ("VariationId", "ProductId", "VariationName", "IsActive") VALUES (11, 1, 'UH-300|Jar|300g', TRUE), (12, 1, 'UH-500|Tub|500g', TRUE);
                INSERT INTO "Stocks" ("VariationId", "LocationId", "Quantity", "MinThreshold", "UpdatedAt") VALUES (11, 8, 2, 10, now()), (12, 8, 50, 10, now());
                """);
            await setup.GetService<IMigrator>().MigrateAsync();
        }

        await using var db = scratch.Context();
        var low = await db.Stocks.SingleAsync(s => s.VariationId == 11);
        Assert.True(low.LowStockAlertOpen);
        Assert.False((await db.Stocks.SingleAsync(s => s.VariationId == 12)).LowStockAlertOpen);

        low.Quantity = 1; // still in the existing shortage: no flood
        await db.SaveChangesAsync();
        Assert.Empty(db.StaffAlerts);
    }
}
