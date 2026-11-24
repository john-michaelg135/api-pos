using Api.Contracts.ProductCatalog;
using Api.Contracts.StockAdjustment;
using Api.Controllers;
using Api.Middlewares;
using Applications.Audit;
using Applications.Services;
using Domains.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ApiPos.Tests;

/// <summary>Starting cash and change, price history across changes, and manager-recorded system losses.</summary>
public class CashAuditPriceHistoryStockLossTests
{
    private static readonly CurrentUserContext Cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);
    private static readonly CurrentUserContext Manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);

    private static void SeedCatalog(PosHarness pos, int stock = 10)
    {
        pos.Db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
        pos.Db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads" });
        pos.Db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UH-300|Jar|300g" });
        pos.Db.ProductPrices.Add(new ProductPrice { PriceId = 1, VariationId = 11, Price = 150m, IsActive = true, EffectiveFrom = DateTime.UtcNow.AddDays(-10), CreatedAt = DateTime.UtcNow.AddDays(-10) });
        pos.Db.Stocks.Add(new Stock { VariationId = 11, LocationId = 8, Quantity = stock, UpdatedAt = DateTime.UtcNow });
        pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = Cashier.PortalUserId!, LocationId = 8, IsPrimary = true });
        pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = Manager.PortalUserId!, LocationId = 8, IsPrimary = true });
        pos.Db.SaveChanges();
    }

    // ── Starting cash and change ──

    [Theory]
    [InlineData(0)]
    [InlineData(299.99)]
    public async Task Starting_cash_below_300_is_refused(double amount)
    {
        using var pos = new PosHarness();
        SeedCatalog(pos);
        pos.SignIn(Cashier);
        var controller = new StartingCashController(pos.Db, new ConfigurationBuilder().Build(), null!, null!, pos.Audit)
        {
            ControllerContext = new ControllerContext { HttpContext = pos.Accessor.HttpContext! },
        };

        var result = await controller.Set(new SetStartingCashDto { LocationId = 8, Amount = (decimal)amount });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("at least ₱300", bad.Value as string);
        Assert.Empty(pos.Db.StartingCashEntries);
    }

    [Fact]
    public async Task Starting_cash_of_300_is_recorded()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos);
        pos.SignIn(Cashier);
        var controller = new StartingCashController(pos.Db, new ConfigurationBuilder().Build(), null!, null!, pos.Audit)
        {
            ControllerContext = new ControllerContext { HttpContext = pos.Accessor.HttpContext! },
        };

        var result = await controller.Set(new SetStartingCashDto { LocationId = 8, Amount = 300m });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(300m, Assert.Single(pos.Db.StartingCashEntries).Amount);
    }

    [Fact]
    public void Change_is_worked_out_from_the_server_total()
    {
        Assert.Equal((500m, 50m), OrderEntryService.CashTender(500m, 450m));
        Assert.Equal((450m, 0m), OrderEntryService.CashTender(null, 450m)); // no tender: exact cash
        var short_ = Assert.Throws<InvalidOperationException>(() => OrderEntryService.CashTender(449.99m, 450m));
        Assert.Contains("not enough", short_.Message);
    }

    [Fact]
    public void Expected_cash_is_starting_plus_tendered_less_change_and_refunds()
    {
        var totals = new ShiftCashTotals(CashSales: 900m, CashRefunds: 150m, NonCashSales: 300m, CashTendered: 1000m, ChangeGiven: 100m);
        Assert.Equal(300m + 1000m - 100m - 150m, totals.ExpectedCash(300m));
        // Tendered less change is the cash sales, so it is the same as starting + sales - refunds.
        Assert.Equal(300m + totals.CashSales - totals.CashRefunds, totals.ExpectedCash(300m));
    }

    // ── Price history ──

    [Fact]
    public async Task Price_history_lists_every_change_with_the_price_it_replaced()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos);
        pos.SignIn(Manager);
        var catalog = new ProductCatalogService(pos.Db, pos.Audit, new FakeStaffIdentity(pos.Accessor));

        await catalog.SetVariationPriceAsync(11, new SetPriceDto { Price = 180m });
        await catalog.SetVariationPriceAsync(11, new SetPriceDto { Price = 180m }); // unchanged: not a new entry
        await catalog.SetVariationPriceAsync(11, new SetPriceDto { Price = 171m });

        var history = await catalog.GetPriceChangesAsync(productId: 1, variationId: null);

        Assert.Equal([171m, 180m, 150m], history.Select(h => h.Price));
        var latest = history[0];
        Assert.True(latest.IsCurrent);
        Assert.Null(latest.EffectiveTo);
        Assert.Equal(180m, latest.PreviousPrice);
        Assert.Equal(-9m, latest.Change);
        Assert.Equal(-5m, latest.ChangePercent);
        Assert.NotNull(latest.SetBy); // the signed-in manager, not a client-sent number
        var raise = history[1];
        Assert.False(raise.IsCurrent);
        Assert.Equal(latest.EffectiveFrom, raise.EffectiveTo);
        Assert.Equal(20m, raise.ChangePercent);
        Assert.Null(history[2].PreviousPrice); // first price
        Assert.Single(pos.Db.ProductPrices, p => p.IsActive);
    }

    [Fact]
    public async Task Price_history_can_be_limited_to_the_items_a_branch_carries()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos);
        pos.Db.ProductVariations.Add(new ProductVariation { VariationId = 12, ProductId = 1, VariationName = "UH-500|Jar|500g" });
        pos.Db.ProductPrices.Add(new ProductPrice { PriceId = 2, VariationId = 12, Price = 220m, IsActive = true, EffectiveFrom = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        pos.Db.SaveChanges();
        var catalog = new ProductCatalogService(pos.Db, pos.Audit);

        var history = await catalog.GetPriceChangesAsync(null, null, variationIds: [11]);

        Assert.Equal(11, Assert.Single(history).VariationId);
    }

    // ── System loss ──

    private static StockAdjustmentService Adjustments(PosHarness pos) =>
        new(pos.Db, new InventoryService(pos.Db, pos.Accessor, null!, pos.Audit), pos.Accessor, pos.Audit, new FakeStaffIdentity(pos.Accessor));

    [Fact]
    public async Task A_recorded_loss_takes_the_units_out_of_stock_and_shows_in_the_ledger()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos, stock: 10);
        pos.SignIn(Manager);
        var service = Adjustments(pos);

        var loss = await service.RecordStockLossAsync(new RecordStockLossDto
        {
            VariationId = 11, LocationId = 8, Quantity = 3, LossReason = "expired", Notes = "Past best-before date",
        });

        Assert.Equal(7, pos.Db.Stocks.Single().Quantity);
        Assert.Equal(StockLossReasons.Expired, loss.LossReason);
        Assert.Equal("Past best-before date", loss.Notes);
        Assert.Equal(3, loss.Quantity);
        Assert.Equal(450m, loss.LossValue);

        var movement = Assert.Single(pos.Db.StockMovements);
        Assert.Equal(InventoryService.StockLossType, movement.MovementType);
        Assert.Equal(-3, movement.QuantityDelta);
        Assert.Equal($"Stock loss #{loss.LossId}", movement.Reference);

        var ledger = await new InventoryService(pos.Db, pos.Accessor, null!, pos.Audit).GetStockMovementsAsync(8, null, null);
        var row = Assert.Single(ledger); // no stand-in "Adjustment" row duplicates it
        Assert.Equal((10, 7), (row.QuantityBefore, row.QuantityAfter));

        Assert.Single(await service.GetStockLossesAsync(8, null, null));
        Assert.Empty(await service.GetAllAdjustmentsAsync()); // listed with losses, not pending adjustments

        var audit = Assert.Single(pos.Writer.Entries);
        Assert.Equal("stock_loss_recorded", audit.Metadata!["operation"]);
    }

    [Fact]
    public async Task A_loss_larger_than_the_stock_on_hand_is_refused()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos, stock: 2);
        pos.SignIn(Manager);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Adjustments(pos).RecordStockLossAsync(new RecordStockLossDto
        {
            VariationId = 11, LocationId = 8, Quantity = 3, LossReason = StockLossReasons.Damaged,
        }));

        Assert.Contains("Only 2 unit(s)", error.Message);
        Assert.Equal(2, pos.Db.Stocks.Single().Quantity);
        Assert.Empty(pos.Db.StockAdjustments);
    }

    [Fact]
    public async Task Other_needs_a_description_and_unknown_reasons_are_refused()
    {
        using var pos = new PosHarness();
        SeedCatalog(pos);
        pos.SignIn(Manager);
        var service = Adjustments(pos);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordStockLossAsync(new RecordStockLossDto
        {
            VariationId = 11, LocationId = 8, Quantity = 1, LossReason = StockLossReasons.Other,
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordStockLossAsync(new RecordStockLossDto
        {
            VariationId = 11, LocationId = 8, Quantity = 1, LossReason = "Stolen?",
        }));
        Assert.Equal(10, pos.Db.Stocks.Single().Quantity);
    }
}
