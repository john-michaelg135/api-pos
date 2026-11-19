using Api.Contracts.OrderManagement;
using Api.Contracts.ProductCatalog;
using Api.Middlewares;
using Applications.Audit;
using Applications.Services;
using Domains.Entities;

namespace ApiPos.Tests;

/// <summary>Workflows through their normal service entry points, with a fake audit writer.</summary>
public class PosAuditWorkflowTests
{
    private static readonly CurrentUserContext Cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);
    private static readonly CurrentUserContext Manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);

    private static (OrderManagementService Orders, Order Order) SeedOrder(PosHarness pos, int quantity = 3)
    {
        pos.Db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
        pos.Db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads" });
        pos.Db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UH-300|Jar|300g" });
        var order = new Order
        {
            OrderId = 4821,
            OrderNumber = "ORD-20261119-0007",
            OrderType = "Walk-in",
            OrderSource = "POS",
            LocationId = 8,
            PaymentMethod = "Cash",
            PaymentStatus = "Paid",
            OrderStatus = "Completed",
            TotalAmount = 450m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        pos.Db.Orders.Add(order);
        pos.Db.OrderItems.Add(new OrderItem { OrderId = 4821, VariationId = 11, Quantity = quantity, UnitPrice = 150m, Subtotal = 150m * quantity });
        pos.Db.PosUserPins.Add(new PosUserPin { AuthUserId = Manager.PortalUserId!, PinCode = "2468" });
        pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = Cashier.PortalUserId!, LocationId = 8, IsPrimary = true });
        pos.Db.PosUserLocations.Add(new PosUserLocation { AuthUserId = Manager.PortalUserId!, LocationId = 8, IsPrimary = true });
        pos.Db.SaveChanges();

        var orders = new OrderManagementService(pos.Db, new NoopInventory(), pos.Accessor, pos.Audit, new FakeStaffIdentity(pos.Accessor));
        return (orders, order);
    }

    [Fact]
    public async Task Cashier_request_and_manager_approval_are_two_entries_on_the_same_order()
    {
        using var pos = new PosHarness();
        var (orders, _) = SeedOrder(pos);

        pos.SignIn(Cashier);
        await orders.RequestRefundAsync(4821, "Wrong flavor", null, [new RefundItemDto { VariationId = 11, Quantity = 1 }]);

        pos.SignIn(Manager);
        // The browser's approvedBy (99) is ignored for attribution.
        await orders.ApproveRefundAsync(4821, approvedBy: 99, pinCode: "2468");

        Assert.Equal(2, pos.Writer.Entries.Count);
        var (request, approval) = (pos.Writer.Entries[0], pos.Writer.Entries[1]);

        Assert.All(pos.Writer.Entries, e =>
        {
            Assert.Equal("POS", e.Source);
            Assert.Equal(PosModules.OrderManagement, e.Module);
            Assert.Equal(AuditActions.Updated, e.Action);
            Assert.Equal("success", e.Outcome);
            Assert.Equal(new Api.Contracts.Shared.AuditIdentity("order", "4821", "Order ORD-20261119-0007"), e.Target);
            Assert.Equal(8, e.Metadata!["location_id"]);
            Assert.Contains("ORD-20261119-0007", e.Description);
            Assert.Contains("Makati Branch", e.Description);
            Assert.EndsWith("Z", e.OccurredAt);
        });

        Assert.Equal(new Api.Contracts.Shared.AuditIdentity("user", "portal-cashier-7", "Ana Cruz"), request.Actor);
        Assert.Equal("refund_requested", request.Metadata!["operation"]);
        Assert.Equal(true, request.Metadata["partial"]);
        Assert.StartsWith("Ana Cruz requested a partial refund", request.Description);

        Assert.Equal(new Api.Contracts.Shared.AuditIdentity("user", "portal-user-42", "Mira Reyes"), approval.Actor);
        Assert.Equal("refund_approved", approval.Metadata!["operation"]);
        Assert.Equal(true, approval.Metadata["partial"]);
        Assert.Equal("Refund Requested", approval.Metadata["previous_status"]);
        Assert.Equal("Completed", approval.Metadata["new_status"]);
        Assert.Equal("Mira Reyes approved a partial refund for Order ORD-20261119-0007 at Makati Branch.", approval.Description);

        Assert.NotEqual(request.IdempotencyKey, approval.IdempotencyKey);
    }

    [Fact]
    public async Task Rejecting_a_refund_is_a_successful_decision()
    {
        using var pos = new PosHarness();
        var (orders, _) = SeedOrder(pos);
        pos.SignIn(Cashier);
        await orders.RequestRefundAsync(4821, "Wrong flavor", null);
        pos.SignIn(Manager);

        await orders.RejectRefundAsync(4821, rejectedBy: 0, reason: "Opened", pinCode: "2468");

        var rejection = pos.Writer.Entries.Last();
        Assert.Equal("refund_rejected", rejection.Metadata!["operation"]);
        Assert.Equal("success", rejection.Outcome);
        Assert.DoesNotContain("Opened", rejection.Description); // free-text remarks stay out
    }

    [Fact]
    public async Task A_denied_PIN_or_self_approval_emits_nothing()
    {
        using var pos = new PosHarness();
        var (orders, _) = SeedOrder(pos);
        pos.SignIn(Cashier);
        await orders.RequestRefundAsync(4821, "Wrong flavor", null);
        pos.Writer.Entries.Clear();

        pos.SignIn(Manager);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.ApproveRefundAsync(4821, 0, "0000"));

        pos.SignIn(Cashier); // the requester has no PIN and could not approve their own request anyway
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.ApproveRefundAsync(4821, 0, "2468"));

        Assert.Empty(pos.Writer.Entries);
    }

    [Fact]
    public async Task An_unchanged_status_request_emits_nothing()
    {
        using var pos = new PosHarness();
        var (orders, _) = SeedOrder(pos);
        pos.SignIn(Manager);

        await orders.UpdateOrderStatusAsync(4821, new UpdateOrderStatusDto { Status = "Completed" });

        Assert.Empty(pos.Writer.Entries);
    }

    [Fact]
    public async Task A_failed_save_emits_nothing_and_a_throwing_writer_keeps_the_result()
    {
        using var pos = new PosHarness();
        var (orders, _) = SeedOrder(pos);
        pos.SignIn(Cashier);
        pos.Writer.Throw = new HttpRequestException("gateway down");

        var result = await orders.RequestRefundAsync(4821, "Wrong flavor", null);

        Assert.Equal("Refund Requested", result!.OrderStatus);
        Assert.Contains(pos.AuditLogger.Messages, m => m.Contains("writer_error:HttpRequestException"));

        // A request refused before commit (already waiting for a manager) never reaches the writer.
        pos.Writer.Throw = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.RequestRefundAsync(4821, "Again", null));
        Assert.Empty(pos.Writer.Entries);
    }

    [Fact]
    public async Task Two_price_edits_are_two_events_with_old_and_new_prices()
    {
        using var pos = new PosHarness();
        SeedOrder(pos);
        pos.Db.ProductPrices.Add(new ProductPrice { VariationId = 11, Price = 150m, IsActive = true, CreatedAt = DateTime.UtcNow });
        pos.Db.SaveChanges();
        pos.SignIn(Manager);
        var catalog = new ProductCatalogService(pos.Db, pos.Audit);

        await catalog.SetVariationPriceAsync(11, new SetPriceDto { Price = 160m });
        await catalog.SetVariationPriceAsync(11, new SetPriceDto { Price = 170m });

        Assert.Equal(2, pos.Writer.Entries.Count);
        Assert.All(pos.Writer.Entries, e => Assert.Equal(PosModules.ProductManagement, e.Module));
        Assert.Equal(150m, pos.Writer.Entries[0].Metadata!["old_price"]);
        Assert.Equal(160m, pos.Writer.Entries[0].Metadata!["new_price"]);
        Assert.Equal(160m, pos.Writer.Entries[1].Metadata!["old_price"]);
        Assert.NotEqual(pos.Writer.Entries[0].IdempotencyKey, pos.Writer.Entries[1].IdempotencyKey);
    }

    [Fact]
    public async Task An_unchanged_product_edit_emits_nothing()
    {
        using var pos = new PosHarness();
        SeedOrder(pos);
        pos.SignIn(Manager);
        var catalog = new ProductCatalogService(pos.Db, pos.Audit);

        await catalog.UpdateProductAsync(1, new UpdateProductDto { ProductName = "Ube Halaya" });
        Assert.Empty(pos.Writer.Entries);

        await catalog.UpdateProductAsync(1, new UpdateProductDto { ProductName = "Ube Halaya Classic" });
        Assert.Equal(AuditActions.Updated, Assert.Single(pos.Writer.Entries).Action);
    }

    [Fact]
    public async Task The_end_of_day_job_closes_a_drawer_as_a_named_process()
    {
        using var pos = new PosHarness();
        SeedOrder(pos);
        pos.Db.StartingCashEntries.Add(new StartingCash
        {
            Id = 5,
            AuthUserId = Cashier.PortalUserId!,
            CashierName = "Ana Cruz",
            LocationId = 8,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(-1),
            Amount = 1000m,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        });
        pos.Db.SaveChanges();
        pos.SignIn(Manager); // the Money Audit page can trigger the sweep; the job still acts

        var closed = await new ShiftAutoCloseService(pos.Db, pos.Audit).CloseOverdueShiftsAsync();

        Assert.Equal(1, closed);
        var entry = Assert.Single(pos.Writer.Entries);
        Assert.Null(entry.Module);
        Assert.Equal(new Api.Contracts.Shared.AuditIdentity("process", "pos-shift-auto-close", "POS end-of-day shift auto-close"), entry.Actor);
        Assert.Equal(new Api.Contracts.Shared.AuditIdentity("starting_cash", "5", entry.Target.DisplayName), entry.Target);
        Assert.Equal("drawer_auto_closed", entry.Metadata!["operation"]);
    }
}
