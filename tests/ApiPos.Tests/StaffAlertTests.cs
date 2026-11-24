using Api.Contracts.OrderManagement;
using Api.Contracts.Scms;
using Api.Controllers;
using Api.Middlewares;
using Applications.Services;
using Applications.StaffAlerts;
using Domains.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApiPos.Tests;

/// <summary>
/// Notification service and Portal directory as POS sees them: copies are keyed by recipient and
/// idempotency key (a replay stores nothing new), and faults can be injected per call.
/// </summary>
public sealed class FakeStaffAlertGateway : IStaffAlertGateway
{
    public sealed class Remote
    {
        public required Guid Id { get; init; }
        public required string UserId { get; init; }
        public required string Key { get; init; }
        public required PortalCopyRequest Request { get; init; }
        public bool Read { get; set; }
    }

    public List<PortalRecipient> Staff { get; } = [];
    public List<Remote> Copies { get; } = [];
    public List<(string UserId, Guid Id)> MarkReads { get; } = [];
    public int SendCalls { get; private set; }
    public int FindCalls { get; private set; }
    public int Nudges { get; private set; }
    public bool DirectoryDown { get; set; }
    public bool GatewayDown { get; set; }
    /// <summary>Store the next copy, then lose the response (timeout after remote insertion).</summary>
    public bool LoseNextSendResponse { get; set; }

    public void Grant(string id, string role, params string[] modules) =>
        Staff.Add(new PortalRecipient(id, id, id, role, modules));

    public void Revoke(string id) => Staff.RemoveAll(s => s.Id == id);

    public Remote? CopyFor(string userId) => Copies.SingleOrDefault(c => c.UserId == userId);

    public Task<IReadOnlyList<PortalRecipient>> RecipientsAsync(CancellationToken cancellationToken)
    {
        if (DirectoryDown) throw new StaffAlertGatewayException("directory_network", transport: true);
        return Task.FromResult<IReadOnlyList<PortalRecipient>>(Staff.ToList());
    }

    public Task<RemoteCopy> SendAsync(PortalCopyRequest copy, CancellationToken cancellationToken)
    {
        Down();
        SendCalls++;
        var existing = Copies.SingleOrDefault(c => c.UserId == copy.UserId && c.Key == copy.IdempotencyKey);
        if (existing is null)
        {
            existing = new Remote { Id = Guid.NewGuid(), UserId = copy.UserId, Key = copy.IdempotencyKey, Request = copy };
            Copies.Add(existing);
        }
        if (LoseNextSendResponse)
        {
            LoseNextSendResponse = false;
            throw new StaffAlertGatewayException("send_timeout", transport: true);
        }
        return Task.FromResult(new RemoteCopy(existing.Id, existing.Read));
    }

    public Task<RemoteCopy?> FindAsync(string userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Down();
        FindCalls++;
        var found = Copies.SingleOrDefault(c => c.UserId == userId && c.Key == idempotencyKey);
        return Task.FromResult(found is null ? null : new RemoteCopy(found.Id, found.Read));
    }

    public Task<RemoteCopy?> StatusAsync(string userId, Guid id, CancellationToken cancellationToken)
    {
        Down();
        var found = Copies.SingleOrDefault(c => c.UserId == userId && c.Id == id);
        return Task.FromResult(found is null ? null : new RemoteCopy(found.Id, found.Read));
    }

    public Task MarkReadAsync(string userId, Guid id, CancellationToken cancellationToken)
    {
        Down();
        MarkReads.Add((userId, id));
        var found = Copies.SingleOrDefault(c => c.UserId == userId && c.Id == id);
        if (found is not null) found.Read = true;
        return Task.CompletedTask;
    }

    public Task NudgeAsync(CancellationToken cancellationToken)
    {
        Down();
        Nudges++;
        return Task.CompletedTask;
    }

    public Task<string> LiveUrlAsync(CancellationToken cancellationToken)
    {
        Down();
        return Task.FromResult("wss://gateway.test/notifications/live?ticket=t");
    }

    private void Down()
    {
        if (GatewayDown) throw new StaffAlertGatewayException("send_network", transport: true);
    }
}

public class StaffAlertTests
{
    private const string OrderModule = StaffAlertAccess.OrderManagementModule;
    private const string StockModule = StaffAlertAccess.StockManagementModule;

    private static readonly CurrentUserContext Cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);
    private static readonly CurrentUserContext Manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);
    private static readonly CurrentUserContext OtherManager = PosHarness.Staff("portal-user-43", "Lito Santos", "Manager", 8);
    private static readonly CurrentUserContext FarManager = PosHarness.Staff("portal-user-90", "Joy Lim", "Manager", 9);

    private static StaffViewer Viewer(CurrentUserContext user, params string[] modules) =>
        new(user.PortalUserId!, user.Role, modules, user.LocationIds);

    private sealed class World : IDisposable
    {
        public PosHarness Pos { get; } = new();
        public FakeStaffAlertGateway Gateway { get; } = new();
        public OrderManagementService Orders { get; }
        public StaffAlertFeed Feed { get; }
        public StaffAlertOptions Options { get; } = new()
        {
            Requested = true,
            GatewayUrl = "https://gateway.test",
            CallerKey = "caller-key",
            PortalUrl = "https://portal.test",
            PortalSystemSecret = "secret",
            WebOrigin = "https://pos.test",
        };

        public World()
        {
            var db = Pos.Db;
            db.Locations.Add(new Location { LocationId = 8, LocationName = "Makati Branch", CreatedAt = DateTime.UtcNow });
            db.Locations.Add(new Location { LocationId = 9, LocationName = "Pasig Branch", CreatedAt = DateTime.UtcNow });
            db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads" });
            db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UH-300|Jar|300g" });
            db.ProductVariations.Add(new ProductVariation { VariationId = 12, ProductId = 1, VariationName = "UH-500|Tub|500g" });
            db.Orders.Add(new Order
            {
                OrderId = 4821, OrderNumber = "ORD-20261119-0007", OrderType = "Walk-in", OrderSource = "POS",
                LocationId = 8, PaymentMethod = "Cash", PaymentStatus = "Paid", OrderStatus = "Completed",
                TotalAmount = 750m, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.OrderItems.Add(new OrderItem { OrderId = 4821, VariationId = 11, Quantity = 3, UnitPrice = 150m, Subtotal = 450m });
            db.OrderItems.Add(new OrderItem { OrderId = 4821, VariationId = 12, Quantity = 1, UnitPrice = 300m, Subtotal = 300m });
            foreach (var manager in new[] { Manager, OtherManager, FarManager })
                db.PosUserPins.Add(new PosUserPin { AuthUserId = manager.PortalUserId!, PinCode = "2468" });
            foreach (var person in new[] { Cashier, Manager, OtherManager, FarManager })
                db.PosUserLocations.Add(new PosUserLocation { AuthUserId = person.PortalUserId!, LocationId = person.LocationIds[0], IsPrimary = true });
            db.SaveChanges();

            Orders = new OrderManagementService(db, new NoopInventory(), Pos.Accessor, Pos.Audit, new FakeStaffIdentity(Pos.Accessor));
            Feed = new StaffAlertFeed(db);

            Gateway.Grant(Cashier.PortalUserId!, "Cashier", OrderModule);
            Gateway.Grant(Manager.PortalUserId!, "Manager", OrderModule, StockModule);
            Gateway.Grant(OtherManager.PortalUserId!, "Manager", OrderModule, StockModule);
            Gateway.Grant(FarManager.PortalUserId!, "Manager", OrderModule, StockModule);
        }

        public StaffAlertDispatcher Dispatcher() =>
            new(Pos.Db, Gateway, Options, NullLogger<StaffAlertDispatcher>.Instance);

        public async Task RequestRefundAsync(params RefundItemDto[] items)
        {
            Pos.SignIn(Cashier);
            await Orders.RequestRefundAsync(4821, "Wrong flavor", null, items);
        }

        /// <summary>Run the worker until nothing is due (copies sent, reads propagated).</summary>
        public async Task DrainAsync()
        {
            for (var i = 0; i < 5; i++)
            {
                await Dispatcher().RunOnceAsync();
                foreach (var copy in Pos.Db.StaffAlertCopies.Where(c => !c.Done)) copy.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                await Pos.Db.SaveChangesAsync();
            }
        }

        public void Dispose() => Pos.Dispose();
    }

    [Fact]
    public async Task A_multi_item_refund_request_is_one_branch_alert_for_eligible_reviewers_only()
    {
        using var w = new World();
        await w.RequestRefundAsync(new RefundItemDto { VariationId = 11, Quantity = 1 }, new RefundItemDto { VariationId = 12, Quantity = 1 });

        var alert = Assert.Single(w.Pos.Db.StaffAlerts);
        Assert.Equal(StaffAlertKinds.RefundRequested, alert.Kind);
        Assert.Equal(StaffAlertAudiences.Branch, alert.Audience);
        Assert.Equal(8, alert.LocationId);
        Assert.Equal(Cashier.PortalUserId, alert.RequesterPortalUserId);
        Assert.True(alert.Partial);
        Assert.Equal("/order-management?orderId=4821", alert.LinkPath);
        Assert.Equal($"order-history:{w.Pos.Db.OrderStatusHistories.Single().Id}", alert.OccurrenceKey);
        Assert.DoesNotContain("₱", alert.Body);

        Assert.Equal(1, await w.Feed.UnreadCountAsync(Viewer(Manager, OrderModule)));
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(FarManager, OrderModule)));       // other branch
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(Manager, StockModule)));          // no Order Management
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(Cashier, OrderModule)));          // own request

        await w.DrainAsync();
        Assert.Single(w.Gateway.Copies, c => c.UserId == Manager.PortalUserId);
        Assert.Single(w.Gateway.Copies, c => c.UserId == OtherManager.PortalUserId);
        Assert.Equal(2, w.Gateway.Copies.Count);
        var copy = w.Gateway.CopyFor(Manager.PortalUserId!)!;
        Assert.Equal("pos.refund-requested.order-history-" + w.Pos.Db.OrderStatusHistories.Single().Id, copy.Key);
        Assert.Equal("https://pos.test/order-management?orderId=4821", copy.Request.Link);
        Assert.True(w.Gateway.Nudges >= 1);
    }

    [Fact]
    public async Task Decisions_reach_only_the_requester_and_a_new_request_is_a_new_occurrence()
    {
        using var w = new World();
        await w.RequestRefundAsync();
        w.Pos.SignIn(Manager);
        await w.Orders.RejectRefundAsync(4821, rejectedBy: 0, reason: "Opened", pinCode: "2468");

        await w.RequestRefundAsync(new RefundItemDto { VariationId = 11, Quantity = 1 });
        w.Pos.SignIn(OtherManager);
        await w.Orders.ApproveRefundAsync(4821, approvedBy: 0, pinCode: "2468");

        var kinds = w.Pos.Db.StaffAlerts.OrderBy(a => a.Id).Select(a => a.Kind).ToList();
        Assert.Equal([StaffAlertKinds.RefundRequested, StaffAlertKinds.RefundRejected, StaffAlertKinds.RefundRequested, StaffAlertKinds.RefundApproved], kinds);

        var approval = w.Pos.Db.StaffAlerts.Single(a => a.Kind == StaffAlertKinds.RefundApproved);
        Assert.Equal(StaffAlertAudiences.Requester, approval.Audience);
        Assert.Equal(Cashier.PortalUserId, approval.RecipientPortalUserId);
        Assert.True(approval.Partial);
        Assert.Contains("partial", approval.Body);

        var cashierFeed = await w.Feed.ListAsync(Viewer(Cashier, OrderModule), null, null, false);
        Assert.Equal(2, cashierFeed.Items.Count); // the two decisions, nothing else
        Assert.All(cashierFeed.Items, i => Assert.True(StaffAlertKinds.IsDecision(i.Kind)));
        var managerFeed = await w.Feed.ListAsync(Viewer(Manager, OrderModule), null, null, false);
        Assert.DoesNotContain(managerFeed.Items, i => StaffAlertKinds.IsDecision(i.Kind));

        // The cashier was offline: the decisions wait as Portal copies for them alone.
        await w.DrainAsync();
        var cashierCopies = w.Gateway.Copies.Where(c => c.UserId == Cashier.PortalUserId).ToList();
        Assert.Equal(2, cashierCopies.Count);
    }

    [Fact]
    public async Task Void_request_and_decision_follow_the_same_rules()
    {
        using var w = new World();
        w.Pos.SignIn(Cashier);
        await w.Orders.RequestVoidAsync(4821, "Customer changed mind");
        w.Pos.SignIn(Manager);
        await w.Orders.ApproveVoidAsync(4821, approvedBy: 0, pinCode: "2468");

        var alerts = w.Pos.Db.StaffAlerts.OrderBy(a => a.Id).ToList();
        Assert.Equal([StaffAlertKinds.VoidRequested, StaffAlertKinds.VoidApproved], alerts.Select(a => a.Kind));
        Assert.Equal(Cashier.PortalUserId, alerts[1].RecipientPortalUserId);
    }

    [Fact]
    public async Task A_decision_with_an_unknown_requester_reaches_no_one()
    {
        using var w = new World();
        w.Pos.SignIn(PosHarness.Staff(null!, "Legacy", "Cashier", 8)); // no Portal identity
        await w.Orders.RequestVoidAsync(4821, "Legacy session");
        w.Pos.SignIn(Manager);
        await w.Orders.RejectVoidAsync(4821, rejectedBy: 0, reason: "No", pinCode: "2468");

        var decision = w.Pos.Db.StaffAlerts.Single(a => a.Kind == StaffAlertKinds.VoidRejected);
        Assert.Null(decision.RecipientPortalUserId);
        foreach (var person in new[] { Cashier, Manager, OtherManager })
            Assert.Equal(0, (await w.Feed.ListAsync(Viewer(person, OrderModule), null, null, false)).Items.Count(i => i.Kind == StaffAlertKinds.VoidRejected));
        await w.DrainAsync();
        Assert.DoesNotContain(w.Gateway.Copies, c => c.Key.StartsWith(StaffAlertKinds.VoidRejected));
    }

    [Fact]
    public async Task A_duplicate_IMS_manifest_raises_one_stock_alert()
    {
        using var w = new World();
        var controller = new TransfersController(w.Pos.Db);
        var manifest = new TransferDetailsDto
        {
            TransferId = "trans_001",
            SourceLocation = new LocationDto { LocationId = "C1", LocationName = "Commissary" },
            DestinationLocation = new DestinationBranchDto { BranchId = "8", BranchName = "Makati" },
            Products = [new TransferProductDto { ProductId = "p1", Sku = "UH-300", ProductName = "Ube Halaya", Quantity = 5 }],
        };
        var stockRequests = new StockRequestService(w.Pos.Db, null!, w.Pos.Accessor, w.Pos.Audit, NullLogger.For<StockRequestService>());
        await controller.ReceiveTransferDetails(manifest, stockRequests);
        await controller.ReceiveTransferDetails(manifest, stockRequests);

        var alert = Assert.Single(w.Pos.Db.StaffAlerts);
        Assert.Equal(StaffAlertKinds.TransferArrived, alert.Kind);
        Assert.Equal(StockModule, alert.Module);
        Assert.Equal("/stock-management?tab=receive&transferId=trans_001", alert.LinkPath);
        Assert.Equal(1, await w.Feed.UnreadCountAsync(Viewer(Manager, StockModule)));
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(Manager, OrderModule)));   // order-only viewer
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(Cashier, OrderModule, StockModule))); // cashiers don't receive
    }

    [Fact]
    public async Task Low_stock_alerts_once_per_episode()
    {
        using var w = new World();
        var db = w.Pos.Db;
        var stock = new Stock { VariationId = 11, LocationId = 8, Quantity = 20, MinThreshold = 10, UpdatedAt = DateTime.UtcNow };
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();
        Assert.Empty(db.StaffAlerts);

        async Task Set(int quantity) { stock.Quantity = quantity; await db.SaveChangesAsync(); }
        await Set(9);   // drops below: episode 1
        await Set(5);   // still low: same episode
        await Set(3);
        Assert.Single(db.StaffAlerts);

        // Reading the low-stock list has no side effects.
        var inventory = new InventoryService(db, w.Pos.Accessor, null!, w.Pos.Audit);
        await inventory.GetLowStockAlertsAsync();
        Assert.Single(db.StaffAlerts);

        await Set(10);  // recovers to the threshold
        await Set(4);   // falls again: episode 2
        var alerts = db.StaffAlerts.OrderBy(a => a.Id).ToList();
        Assert.Equal(["low-stock:8:11:1", "low-stock:8:11:2"], alerts.Select(a => a.OccurrenceKey));
        Assert.Equal("Ube Halaya (Jar · 300g) at Makati Branch is below its minimum stock level.", alerts[0].Body);
        Assert.Equal(2, await w.Feed.UnreadCountAsync(Viewer(Manager, StockModule)));
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(FarManager, StockModule)));
    }

    [Fact]
    public async Task A_timeout_after_remote_insertion_and_a_restart_recover_the_same_remote_copy()
    {
        using var w = new World();
        await w.RequestRefundAsync();
        w.Gateway.LoseNextSendResponse = true;

        await w.Dispatcher().RunOnceAsync();          // first send stored remotely, response lost
        Assert.Contains(w.Pos.Db.StaffAlertCopies, c => c.RemoteId == null && c.Attempts == 1);

        await w.DrainAsync();                          // fresh dispatcher instances = restarts
        Assert.Equal(2, w.Gateway.Copies.Count);       // one per eligible manager, never two
        foreach (var copy in w.Pos.Db.StaffAlertCopies)
            Assert.Equal(w.Gateway.CopyFor(copy.PortalUserId)!.Id, copy.RemoteId);
    }

    [Fact]
    public async Task An_outage_keeps_the_saved_alert_and_revocation_during_it_prevents_a_new_copy()
    {
        using var w = new World();
        w.Gateway.DirectoryDown = true;
        await w.RequestRefundAsync();
        await w.Dispatcher().RunOnceAsync();

        var alert = w.Pos.Db.StaffAlerts.Single();
        Assert.Null(alert.AudienceResolvedAt);                       // audience waits for the directory
        Assert.Equal(1, await w.Feed.UnreadCountAsync(Viewer(Manager, OrderModule)));

        // Audience resolves, then the gateway goes down before delivery; OtherManager loses the Module.
        w.Gateway.DirectoryDown = false;
        w.Gateway.GatewayDown = true;
        await w.Dispatcher().RunOnceAsync();
        Assert.Equal(2, w.Pos.Db.StaffAlertCopies.Count());
        w.Gateway.Revoke(OtherManager.PortalUserId!);
        w.Gateway.Grant(OtherManager.PortalUserId!, "Manager", StockModule);
        w.Gateway.GatewayDown = false;
        await w.DrainAsync();

        Assert.NotNull(w.Gateway.CopyFor(Manager.PortalUserId!));
        Assert.Null(w.Gateway.CopyFor(OtherManager.PortalUserId!));
        Assert.Equal(StaffAlertCopyStates.Withheld,
            w.Pos.Db.StaffAlertCopies.Single(c => c.PortalUserId == OtherManager.PortalUserId).State);
        // In POS the bell follows the session's current grants.
        Assert.Equal(0, await w.Feed.UnreadCountAsync(Viewer(OtherManager, StockModule)));
    }

    [Fact]
    public async Task Reads_converge_across_linked_copies_and_never_cross_branches()
    {
        using var w = new World();
        await w.RequestRefundAsync();
        w.Pos.SignIn(Manager);
        await w.Orders.RejectRefundAsync(4821, rejectedBy: 0, reason: "Opened", pinCode: "2468");
        await w.RequestRefundAsync(new RefundItemDto { VariationId = 11, Quantity = 1 });
        await w.DrainAsync();
        var requests = w.Pos.Db.StaffAlerts.Where(a => a.Kind == StaffAlertKinds.RefundRequested).OrderBy(a => a.Id).ToList();
        var unrelated = new FakeStaffAlertGateway.Remote
        {
            Id = Guid.NewGuid(), UserId = Manager.PortalUserId!, Key = "hrms.leave",
            Request = new PortalCopyRequest("hrms.leave", "hrms.leave", "Leave", "Leave", null, Manager.PortalUserId!),
        };
        w.Gateway.Copies.Add(unrelated);

        // Another branch's manager cannot mark it read.
        Assert.False(await w.Feed.MarkReadAsync(Viewer(FarManager, OrderModule), requests[0].Id));
        Assert.Null(w.Pos.Db.StaffAlerts.Single(a => a.Id == requests[0].Id).ReadAt);

        // Read the first request in POS: both managers' Portal copies become read.
        Assert.True(await w.Feed.MarkReadAsync(Viewer(Manager, OrderModule), requests[0].Id));
        await w.DrainAsync();
        Assert.All(w.Gateway.Copies.Where(c => c.Key.EndsWith($"-{requests[0].SourceHistoryId}")), c => Assert.True(c.Read));

        // Read the second from OtherManager's Portal copy: POS and Manager's copy follow.
        w.Gateway.Copies.Single(c => c.UserId == OtherManager.PortalUserId && c.Key.EndsWith($"-{requests[1].SourceHistoryId}")).Read = true;
        await w.DrainAsync();
        Assert.NotNull(w.Pos.Db.StaffAlerts.Single(a => a.Id == requests[1].Id).ReadAt);
        Assert.True(w.Gateway.Copies.Single(c => c.UserId == Manager.PortalUserId && c.Key.EndsWith($"-{requests[1].SourceHistoryId}")).Read);

        // The cashier's personal decision and the unrelated Portal message are untouched.
        Assert.Null(w.Pos.Db.StaffAlerts.Single(a => a.Kind == StaffAlertKinds.RefundRejected).ReadAt);
        Assert.False(w.Gateway.CopyFor(Cashier.PortalUserId!)!.Read);
        Assert.False(unrelated.Read);
        Assert.DoesNotContain(w.Gateway.MarkReads, m => m.Id == unrelated.Id);
    }

    [Fact]
    public async Task Mark_all_read_touches_only_what_the_viewer_can_see()
    {
        using var w = new World();
        await w.RequestRefundAsync();
        w.Pos.Db.Stocks.Add(new Stock { VariationId = 11, LocationId = 9, Quantity = 1, MinThreshold = 5, UpdatedAt = DateTime.UtcNow });
        await w.Pos.Db.SaveChangesAsync();

        Assert.Equal(1, await w.Feed.MarkAllReadAsync(Viewer(Manager, OrderModule, StockModule)));
        Assert.Null(w.Pos.Db.StaffAlerts.Single(a => a.Kind == StaffAlertKinds.LowStock).ReadAt); // Pasig's
        Assert.Equal(1, await w.Feed.UnreadCountAsync(Viewer(FarManager, StockModule)));
    }

    [Fact]
    public async Task The_feed_pages_with_a_cursor()
    {
        using var w = new World();
        var db = w.Pos.Db;
        for (var i = 0; i < 5; i++)
        {
            db.StaffAlerts.Add(new StaffAlert
            {
                OccurrenceKey = $"test:{i}", Kind = StaffAlertKinds.LowStock, Module = StockModule,
                Audience = StaffAlertAudiences.Branch, LocationId = 8, Title = "t", Body = "b", LinkPath = "/", CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var viewer = Viewer(Manager, StockModule);
        var first = await w.Feed.ListAsync(viewer, null, 2, false);
        var second = await w.Feed.ListAsync(viewer, first.NextCursor, 2, false);
        var third = await w.Feed.ListAsync(viewer, second.NextCursor, 2, false);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.Id).Distinct().Count());
        Assert.Null(third.NextCursor);
        Assert.Equal(5, first.Unread);
    }

    [Fact]
    public async Task Paused_sends_make_no_remote_calls_and_resume_later()
    {
        using var w = new World();
        await w.RequestRefundAsync();
        var paused = new StaffAlertDispatcher(w.Pos.Db, w.Gateway, new StaffAlertOptions { Requested = false, GatewayUrl = "https://gateway.test", CallerKey = "k" },
            NullLogger<StaffAlertDispatcher>.Instance);
        await paused.RunOnceAsync();
        Assert.Empty(w.Gateway.Copies);
        Assert.Equal(0, w.Gateway.Nudges);

        await w.DrainAsync();
        Assert.Equal(2, w.Gateway.Copies.Count);
    }
}
