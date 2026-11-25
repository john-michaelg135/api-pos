using System.Net;
using System.Text.Json;
using Api.Contracts.Scms;
using Api.Contracts.StockRequests;
using Api.Controllers;
using Api.Middlewares;
using Applications.Services;
using Applications.StaffAlerts;
using Domains.Entities;
using Infrastructures.Externals;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ApiPos.Tests;

/// <summary>Branch stock requests to IMS, end to end: submit, retry, decision, manifest link, receipt (STOCK-REQUEST-INTEGRATION.md §8).</summary>
public class StockRequestTests
{
    private static readonly CurrentUserContext Manager = PosHarness.Staff("portal-user-42", "Mira Reyes", "Manager", 8);
    private static readonly CurrentUserContext OtherBranchManager = PosHarness.Staff("portal-user-77", "Jo Santos", "Manager", 9);
    private static readonly CurrentUserContext Ims = new()
    {
        Username = "scms-service",
        DisplayName = "Supply Chain System",
        Role = StockRequestsController.ServiceRole,
        IsSuperUser = true,
        Apps = ["SCMS"],
    };

    /// <summary>A fake IMS: answers with whatever <see cref="Respond"/> says and records every body.</summary>
    private sealed class FakeIms : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Created("req_551");
        public List<(string Path, string Body)> Calls { get; } = [];

        public static HttpResponseMessage Created(string imsId) => new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"success\":true,\"data\":{\"requestId\":\"x\",\"imsRequestId\":\"" + imsId + "\",\"status\":\"Submitted\"}}"),
        };

        public static HttpResponseMessage Status(HttpStatusCode status, string body = "") => new(status) { Content = new StringContent(body) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return Respond(request);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public PosHarness Pos { get; } = new();
        public FakeIms Ims { get; } = new();
        public ScmsApiClient Client { get; }
        public StockRequestService Service { get; }

        public Fixture()
        {
            Client = new ScmsApiClient(new HttpClient(Ims) { BaseAddress = new Uri("https://ims.test") });
            Service = new StockRequestService(Pos.Db, Client, Pos.Accessor, Pos.Audit, NullLogger.For<StockRequestService>());

            var db = Pos.Db;
            db.Locations.Add(new Location { LocationId = 8, LocationName = "TriNoma Bazaar", LocationType = "Bazaar", CreatedAt = DateTime.UtcNow });
            db.Locations.Add(new Location { LocationId = 9, LocationName = "Makati Store", CreatedAt = DateTime.UtcNow });
            db.Locations.Add(new Location { LocationId = 1, LocationName = "Commissary", LocationType = "Commissary", CreatedAt = DateTime.UtcNow });
            db.Products.Add(new Product { ProductId = 1, ProductName = "Ube Halaya", ProductCategory = "Spreads", ScmsProductId = "prod_018" });
            db.ProductVariations.Add(new ProductVariation { VariationId = 11, ProductId = 1, VariationName = "UBE-HAL-250|Pouch|250g" });
            db.ProductVariations.Add(new ProductVariation { VariationId = 12, ProductId = 1, VariationName = "Jar 500g" }); // no SKU
            db.Stocks.Add(new Stock { VariationId = 11, LocationId = 8, Quantity = 5, UpdatedAt = DateTime.UtcNow });
            db.PosUserLocations.Add(new PosUserLocation { AuthUserId = Manager.PortalUserId!, LocationId = 8, IsPrimary = true });
            db.PosUserLocations.Add(new PosUserLocation { AuthUserId = OtherBranchManager.PortalUserId!, LocationId = 9, IsPrimary = true });
            db.SaveChanges();
            Pos.SignIn(Manager);
        }

        public StockRequestsController Controller(CurrentUserContext? user)
        {
            Pos.SignIn(user);
            return new StockRequestsController(Service) { ControllerContext = new ControllerContext { HttpContext = Pos.Accessor.HttpContext! } };
        }

        public TransfersController Transfers()
        {
            Pos.SignIn(Ims_);
            return new TransfersController(Pos.Db) { ControllerContext = new ControllerContext { HttpContext = Pos.Accessor.HttpContext! } };
        }

        private static CurrentUserContext Ims_ => StockRequestTests.Ims;

        public async Task<StockRequestResponseDto> Request(int quantity = 6, string? clientRequestId = null) =>
            (await Service.CreateAsync(new CreateStockRequestDto
            {
                VariationId = 11,
                LocationId = 8,
                Quantity = quantity,
                Notes = "Weekend bazaar, expecting double the usual sales.",
                ClientRequestId = clientRequestId,
            })).Request;

        /// <summary>IMS answers submits as usual and cancels with <paramref name="cancel"/>.</summary>
        public void ImsCancelAnswers(Func<HttpResponseMessage> cancel)
        {
            var submit = Ims.Respond;
            Ims.Respond = request => request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/cancel")
                ? cancel()
                : submit(request);
        }

        public Task<StockRequestResponseDto> Cancel(string requestId, string? reason = "Got stock from Makati instead.")
        {
            Pos.SignIn(Manager);
            return Service.CancelAsync(requestId, new CancelStockRequestDto { Reason = reason });
        }

        public Task<StockRequestResponseDto> Approve(string requestId, int quantity)
        {
            Pos.SignIn(StockRequestTests.Ims);
            return Service.ApplyDecisionAsync(requestId, Decision("Approved", quantity));
        }

        public void Dispose() => Pos.Dispose();
    }

    private static StockRequestDecisionDto Decision(string status, int? quantity = null, string? remarks = null) => new()
    {
        Status = status,
        ApprovedQuantity = quantity,
        Remarks = remarks,
        ImsRequestId = "req_551",
        DecidedBy = "ims_u_12",
        DecidedByName = "Mark Reyes",
        DecidedAt = DateTime.UtcNow,
    };

    private static TransferDetailsDto Manifest(string? stockRequestId, string branchId = "8", string sku = "UBE-HAL-250", int quantity = 4) => new()
    {
        TransferId = "trans_900",
        SourceLocation = new LocationDto { LocationId = "1", LocationName = "Commissary" },
        DestinationLocation = new DestinationBranchDto { BranchId = branchId, BranchName = "TriNoma Bazaar" },
        Products = [new TransferProductDto { ProductId = "prod_018", Sku = sku, ProductName = "Ube Halaya", Quantity = quantity }],
        TransferDate = DateTime.UtcNow,
        Status = "In Transit",
        StockRequestId = stockRequestId,
    };

    // ── Submit ──

    [Fact]
    public async Task A_request_is_sent_to_IMS_and_marked_submitted()
    {
        using var f = new Fixture();

        var created = await f.Request();

        Assert.Equal("SR-000001", created.RequestId);
        Assert.Equal(StockRequestStatuses.Submitted, created.Status);
        var saved = Assert.Single(f.Pos.Db.StockRequests);
        Assert.Equal("req_551", saved.ImsRequestId);
        Assert.Equal(1, saved.SendAttempts);

        var (path, body) = Assert.Single(f.Ims.Calls);
        Assert.Equal("/api/pos/stock-requests", path);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("SR-000001", root.GetProperty("requestId").GetString());
        Assert.Equal("8", root.GetProperty("branch").GetProperty("branchId").GetString());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal("UBE-HAL-250", item.GetProperty("sku").GetString());
        Assert.Equal("prod_018", item.GetProperty("productId").GetString());
        Assert.Equal("Pouch 250g", item.GetProperty("variationName").GetString());
        Assert.Equal(6, item.GetProperty("quantity").GetInt32());
        Assert.Equal(5, item.GetProperty("currentStock").GetInt32());
        Assert.Equal("portal-user-42", root.GetProperty("requestedBy").GetString());
        Assert.Equal("Mira Reyes", root.GetProperty("requestedByName").GetString());
    }

    [Fact]
    public async Task IMS_down_keeps_the_request_sending_and_the_sweep_delivers_it_later()
    {
        using var f = new Fixture();
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.ServiceUnavailable);

        var created = await f.Request();
        Assert.Equal(StockRequestStatuses.Sending, created.Status);
        Assert.Contains("503", created.LastSendError);

        f.Ims.Respond = _ => FakeIms.Created("req_600");
        Assert.Equal(1, await f.Service.RetryPendingAsync());

        var saved = Assert.Single(f.Pos.Db.StockRequests);
        Assert.Equal(StockRequestStatuses.Submitted, saved.Status);
        Assert.Equal("req_600", saved.ImsRequestId);
        Assert.Null(saved.LastSendError);
        Assert.Equal(2, saved.SendAttempts);
        // Every attempt carries the same requestId, so IMS can dedupe.
        Assert.All(f.Ims.Calls, c => Assert.Contains("\"requestId\":\"SR-000001\"", c.Body));
    }

    [Fact]
    public async Task The_sweep_gives_up_after_the_attempt_cap()
    {
        using var f = new Fixture();
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.InternalServerError);
        await f.Request();

        for (var i = 0; i < StockRequestService.MaxSendAttempts + 3; i++) await f.Service.RetryPendingAsync();

        Assert.Equal(StockRequestService.MaxSendAttempts, f.Ims.Calls.Count);
        Assert.Equal(StockRequestStatuses.Sending, Assert.Single(f.Pos.Db.StockRequests).Status);
    }

    [Fact]
    public async Task IMS_refusing_the_submit_rejects_it_with_the_reason()
    {
        using var f = new Fixture();
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.BadRequest, """{"success":false,"message":"items[0].sku UBE-HAL-250 is not an IMS product."}""");

        var created = await f.Request();

        Assert.Equal(StockRequestStatuses.Rejected, created.Status);
        Assert.Equal("items[0].sku UBE-HAL-250 is not an IMS product.", created.Remarks);

        // Rejected is final, so the branch may ask again.
        f.Ims.Respond = _ => FakeIms.Created("req_700");
        Assert.Equal(StockRequestStatuses.Submitted, (await f.Request()).Status);
    }

    [Theory]
    [InlineData(0, "Need more")]
    [InlineData(-3, "Need more")]
    [InlineData(10001, "Need more")]
    [InlineData(5, "")]
    [InlineData(5, "   ")]
    [InlineData(5, "<script>alert(1)</script>")]
    public async Task Bad_quantities_and_notes_are_refused(int quantity, string notes)
    {
        using var f = new Fixture();

        var ex = await Assert.ThrowsAsync<StockRequestException>(() =>
            f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = quantity, Notes = notes }));

        Assert.Equal(400, ex.Status);
        Assert.Empty(f.Pos.Db.StockRequests);
        Assert.Empty(f.Ims.Calls);
    }

    [Fact]
    public async Task The_commissary_and_items_without_a_SKU_cannot_be_requested()
    {
        using var f = new Fixture();
        f.Pos.SignIn(Ims); // an administrator-level user, so branch access isn't what refuses it

        var commissary = await Assert.ThrowsAsync<StockRequestException>(() =>
            f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 11, LocationId = 1, Quantity = 2, Notes = "x" }));
        Assert.Contains("Commissary", commissary.Message);

        var noSku = await Assert.ThrowsAsync<StockRequestException>(() =>
            f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 12, LocationId = 8, Quantity = 2, Notes = "x" }));
        Assert.Contains("SKU", noSku.Message);
    }

    [Fact]
    public async Task A_second_open_request_for_the_same_item_is_a_new_request()
    {
        using var f = new Fixture();
        var first = await f.Request();

        var second = await f.Request(2);

        Assert.Equal("SR-000002", second.RequestId);
        Assert.Equal(StockRequestStatuses.Submitted, second.Status);
        Assert.Equal(2, f.Pos.Db.StockRequests.Count(r => r.Status == StockRequestStatuses.Submitted));
        Assert.Equal(2, f.Ims.Calls.Count);
        Assert.Equal(2, (await f.Service.ListAsync(8, 11, openOnly: true)).Count);

        // Each one is decided and shipped on its own.
        await f.Approve(first.RequestId, 6);
        Assert.Equal(StockRequestStatuses.Submitted, f.Pos.Db.StockRequests.Single(r => r.RequestId == second.RequestId).Status);
    }

    [Fact]
    public async Task The_same_form_submitted_twice_makes_one_request()
    {
        using var f = new Fixture();
        var key = Guid.NewGuid().ToString();

        var first = await f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 6, Notes = "x", ClientRequestId = key });
        var again = await f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 6, Notes = "x", ClientRequestId = key });

        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.Equal(first.Request.RequestId, again.Request.RequestId);
        Assert.Single(f.Pos.Db.StockRequests);
        Assert.Single(f.Ims.Calls);

        var reused = await Assert.ThrowsAsync<StockRequestException>(() =>
            f.Service.CreateAsync(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 9, Notes = "x", ClientRequestId = key }));
        Assert.Equal(409, reused.Status);
    }

    [Fact]
    public async Task A_replayed_form_answers_200_and_a_new_one_201()
    {
        using var f = new Fixture();
        var dto = new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 6, Notes = "x", ClientRequestId = "form-1" };

        Assert.Equal(201, Assert.IsAssignableFrom<ObjectResult>(await f.Controller(Manager).Create(dto)).StatusCode);
        Assert.IsType<OkObjectResult>(await f.Controller(Manager).Create(dto));
    }

    // ── Cancel ──

    [Fact]
    public async Task A_submitted_request_is_cancelled_once_IMS_confirms()
    {
        using var f = new Fixture();
        var created = await f.Request();
        f.ImsCancelAnswers(() => FakeIms.Status(HttpStatusCode.OK, "{\"success\":true}"));

        var cancelled = await f.Cancel(created.RequestId);

        Assert.Equal(StockRequestStatuses.Cancelled, cancelled.Status);
        Assert.False(cancelled.CanCancel);
        Assert.Equal("Mira Reyes", cancelled.CancelledByName);
        Assert.Equal("Got stock from Makati instead.", cancelled.CancelReason);
        var (path, body) = f.Ims.Calls.Last();
        Assert.Equal($"/api/pos/stock-requests/{created.RequestId}/cancel", path);
        Assert.Contains("\"reason\":\"Got stock from Makati instead.\"", body);
        Assert.Contains("\"cancelledBy\":\"portal-user-42\"", body);

        // Cancelled is final: a late IMS decision is refused, and cancelling again changes nothing.
        f.Pos.SignIn(Ims);
        var late = await Assert.ThrowsAsync<StockRequestException>(() => f.Service.ApplyDecisionAsync(created.RequestId, Decision("Approved", 6)));
        Assert.Equal(409, late.Status);
        var calls = f.Ims.Calls.Count;
        Assert.Equal(StockRequestStatuses.Cancelled, (await f.Cancel(created.RequestId)).Status);
        Assert.Equal(calls, f.Ims.Calls.Count);
    }

    [Fact]
    public async Task A_request_IMS_never_received_is_cancelled_and_no_longer_resent()
    {
        using var f = new Fixture();
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.ServiceUnavailable);
        var created = await f.Request();
        Assert.Equal(StockRequestStatuses.Sending, created.Status);
        Assert.True(created.CanCancel);

        f.Ims.Respond = _ => FakeIms.Created("req_1");
        f.ImsCancelAnswers(() => FakeIms.Status(HttpStatusCode.NotFound, "{\"message\":\"unknown\"}"));
        Assert.Equal(StockRequestStatuses.Cancelled, (await f.Cancel(created.RequestId, reason: null)).Status);

        var calls = f.Ims.Calls.Count;
        await f.Service.RetryPendingAsync();
        Assert.Equal(calls, f.Ims.Calls.Count);
    }

    [Fact]
    public async Task IMS_refusing_the_cancel_leaves_the_request_unchanged()
    {
        using var f = new Fixture();
        var created = await f.Request();
        f.ImsCancelAnswers(() => FakeIms.Status(HttpStatusCode.Conflict, "{\"success\":false,\"message\":\"SR-000001 was approved at 06:58.\"}"));

        var ex = await Assert.ThrowsAsync<StockRequestException>(() => f.Cancel(created.RequestId));

        Assert.Equal(409, ex.Status);
        Assert.Contains("approved at 06:58", ex.Message);
        Assert.Equal(StockRequestStatuses.Submitted, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task IMS_down_refuses_the_cancel_and_changes_nothing()
    {
        using var f = new Fixture();
        var created = await f.Request();
        f.ImsCancelAnswers(() => FakeIms.Status(HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<StockRequestException>(() => f.Cancel(created.RequestId));

        Assert.Equal(503, ex.Status);
        var saved = f.Pos.Db.StockRequests.Single();
        Assert.Equal(StockRequestStatuses.Submitted, saved.Status);
        Assert.Null(saved.CancelledAt);
    }

    [Fact]
    public async Task An_approved_request_cannot_be_cancelled_and_IMS_is_not_asked()
    {
        using var f = new Fixture();
        var created = await f.Request();
        await f.Approve(created.RequestId, 6);
        var calls = f.Ims.Calls.Count;

        var ex = await Assert.ThrowsAsync<StockRequestException>(() => f.Cancel(created.RequestId));

        Assert.Equal(409, ex.Status);
        Assert.Equal(calls, f.Ims.Calls.Count);
        Assert.Equal(StockRequestStatuses.Approved, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task A_cancelled_request_cannot_be_shipped()
    {
        using var f = new Fixture();
        var created = await f.Request();
        f.ImsCancelAnswers(() => FakeIms.Status(HttpStatusCode.OK));
        await f.Cancel(created.RequestId);

        var result = await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(f.Pos.Db.StockTransfers);
    }

    [Fact]
    public async Task Staff_cannot_cancel_another_branch_request()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var result = await f.Controller(OtherBranchManager).Cancel(created.RequestId, new CancelStockRequestDto());

        Assert.Equal(404, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(StockRequestStatuses.Submitted, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task A_cancel_reason_with_markup_is_refused()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var ex = await Assert.ThrowsAsync<StockRequestException>(() => f.Cancel(created.RequestId, "<b>no</b>"));

        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Branch_staff_cannot_request_or_list_for_another_branch()
    {
        using var f = new Fixture();
        await f.Request();

        var create = await f.Controller(OtherBranchManager).Create(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 1, Notes = "x" });
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(create).StatusCode);

        var list = await f.Controller(OtherBranchManager).List(null, 11);
        Assert.Empty(Assert.IsType<List<StockRequestResponseDto>>(Assert.IsType<OkObjectResult>(list).Value));

        var mine = await f.Controller(Manager).List(8, 11);
        Assert.Single(Assert.IsType<List<StockRequestResponseDto>>(Assert.IsType<OkObjectResult>(mine).Value));
    }

    [Fact]
    public async Task A_cashier_cannot_request_stock()
    {
        using var f = new Fixture();
        var cashier = PosHarness.Staff("portal-cashier-7", "Ana Cruz", "Cashier", 8);

        var result = await f.Controller(cashier).Create(new CreateStockRequestDto { VariationId = 11, LocationId = 8, Quantity = 1, Notes = "x" });

        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }

    // ── IMS decision ──

    [Fact]
    public async Task IMS_can_approve_part_of_a_request_and_a_repeat_changes_nothing()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var controller = f.Controller(Ims);
        var first = await controller.Decide(created.RequestId, Decision("Approved", 4, "Will ship with Thursday's run."));
        Assert.IsType<OkObjectResult>(first);
        var saved = Assert.Single(f.Pos.Db.StockRequests);
        Assert.Equal(StockRequestStatuses.Approved, saved.Status);
        Assert.Equal(4, saved.ApprovedQuantity);
        Assert.Equal("Mark Reyes", saved.DecidedByName);

        var again = await controller.Decide(created.RequestId, Decision("Approved", 4));
        Assert.IsType<OkObjectResult>(again);

        var reject = await controller.Decide(created.RequestId, Decision("Rejected", remarks: "Changed our mind"));
        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(reject).StatusCode);
        Assert.Equal(StockRequestStatuses.Approved, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task IMS_rejecting_needs_remarks_and_frees_the_item_for_a_new_request()
    {
        using var f = new Fixture();
        var created = await f.Request();
        var controller = f.Controller(Ims);

        var noRemarks = await controller.Decide(created.RequestId, Decision("Rejected"));
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(noRemarks).StatusCode);

        var ok = await controller.Decide(created.RequestId, Decision("Rejected", remarks: "No stock at the Commissary this week."));
        Assert.IsType<OkObjectResult>(ok);

        f.Pos.SignIn(Manager);
        Assert.Equal(StockRequestStatuses.Submitted, (await f.Request(3)).Status);
    }

    [Theory]
    [InlineData("Approved", 0)]
    [InlineData("Approved", 7)]
    [InlineData("Approved", null)]
    [InlineData("Maybe", 3)]
    public async Task Invalid_decisions_are_refused(string status, int? quantity)
    {
        using var f = new Fixture();
        var created = await f.Request();

        var result = await f.Controller(Ims).Decide(created.RequestId, Decision(status, quantity));

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(StockRequestStatuses.Submitted, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task Decisions_need_the_IMS_identity_and_a_known_request()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var staff = await f.Controller(Manager).Decide(created.RequestId, Decision("Approved", 6));
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(staff).StatusCode);

        var anonymous = await f.Controller(null).Decide(created.RequestId, Decision("Approved", 6));
        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(anonymous).StatusCode);

        var unknown = await f.Controller(Ims).Decide("SR-999999", Decision("Approved", 6));
        Assert.Equal(404, Assert.IsAssignableFrom<ObjectResult>(unknown).StatusCode);
    }

    [Fact]
    public async Task A_decision_that_beats_the_submit_acknowledgement_still_applies()
    {
        using var f = new Fixture();
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.GatewayTimeout);
        var created = await f.Request(); // POS never saw IMS's 201

        await f.Approve(created.RequestId, 6);
        f.Ims.Respond = _ => FakeIms.Created("req_551");
        await f.Service.RetryPendingAsync();

        Assert.Equal(StockRequestStatuses.Approved, f.Pos.Db.StockRequests.Single().Status);
    }

    // ── Delivery ──

    [Fact]
    public async Task A_manifest_for_an_approved_request_moves_it_in_transit()
    {
        using var f = new Fixture();
        var created = await f.Request();
        await f.Approve(created.RequestId, 4);

        var result = await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service);

        Assert.IsType<OkObjectResult>(result);
        var request = f.Pos.Db.StockRequests.Single();
        Assert.Equal(StockRequestStatuses.InTransit, request.Status);
        Assert.Equal("trans_900", request.TransferId);
        Assert.Equal(created.RequestId, f.Pos.Db.StockTransfers.Single().StockRequestId);

        // IMS re-posting the same manifest is still fine.
        Assert.IsType<OkObjectResult>(await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service));
    }

    [Theory]
    [InlineData("SR-999999", "8", "UBE-HAL-250")] // unknown request
    [InlineData(null, "9", "UBE-HAL-250")]        // wrong branch (request id filled in below)
    [InlineData(null, "8", "OTHER-SKU")]          // manifest lacks the requested SKU
    public async Task Bad_manifest_links_are_refused_and_nothing_is_saved(string? requestId, string branchId, string sku)
    {
        using var f = new Fixture();
        var created = await f.Request();
        await f.Approve(created.RequestId, 4);

        var result = await f.Transfers().ReceiveTransferDetails(Manifest(requestId ?? created.RequestId, branchId, sku), f.Service);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(f.Pos.Db.StockTransfers);
        Assert.Equal(StockRequestStatuses.Approved, f.Pos.Db.StockRequests.Single().Status);
    }

    [Fact]
    public async Task A_manifest_for_a_request_IMS_has_not_approved_is_refused()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var result = await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(f.Pos.Db.StockTransfers);
    }

    [Fact]
    public async Task Receiving_the_delivery_fulfils_the_request_and_tells_IMS()
    {
        using var f = new Fixture();
        var created = await f.Request();
        await f.Approve(created.RequestId, 4);
        await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service);

        f.Pos.SignIn(Manager);
        f.Ims.Respond = _ => FakeIms.Status(HttpStatusCode.OK, "{}");
        var receiving = new TransferReceivingService(f.Pos.Db, f.Client, f.Pos.Accessor, NullLogger.For<TransferReceivingService>(), f.Pos.Audit);
        var line = f.Pos.Db.StockTransferItems.Single();
        await receiving.ReceiveAsync("trans_900", new ReceiveTransferRequest
        {
            Lines = [new ReceiveTransferLineRequest { LineId = line.Id, ReceivedQuantity = 3, DamagedQuantity = 1, DiscrepancyReason = "One pouch torn" }],
        });

        var request = f.Pos.Db.StockRequests.Single();
        Assert.Equal(StockRequestStatuses.Fulfilled, request.Status);
        Assert.Equal(3, request.ReceivedQuantity);
        Assert.NotNull(request.FulfilledAt);
        Assert.Equal(5 + 3, f.Pos.Db.Stocks.Single(s => s.VariationId == 11 && s.LocationId == 8).Quantity);

        var confirmation = f.Ims.Calls.Last();
        Assert.Equal("/api/scms/transfers/trans_900/receive-confirmation", confirmation.Path);
        Assert.Contains($"\"stockRequestId\":\"{created.RequestId}\"", confirmation.Body);
    }

    // ── Decision alerts (issue #13) ──

    private static StaffViewer StockViewer(CurrentUserContext user, params string[] modules) =>
        new(user.PortalUserId!, user.Role, modules.Length == 0 ? [StaffAlertAccess.StockManagementModule] : modules, user.LocationIds);

    [Fact]
    public async Task A_partial_approval_alerts_only_the_requester_once_with_the_approved_quantity()
    {
        using var f = new Fixture();
        var created = await f.Request(12);

        var controller = f.Controller(Ims);
        await controller.Decide(created.RequestId, Decision("Approved", 8));
        await controller.Decide(created.RequestId, Decision("Approved", 8)); // IMS retry

        var alert = Assert.Single(f.Pos.Db.StaffAlerts);
        Assert.Equal(StaffAlertKinds.StockRequestApproved, alert.Kind);
        Assert.Equal(StaffAlertAccess.StockManagementModule, alert.Module);
        Assert.Equal(StaffAlertAudiences.Requester, alert.Audience);
        Assert.Equal(Manager.PortalUserId, alert.RecipientPortalUserId);
        Assert.Equal(8, alert.LocationId);
        Assert.Null(alert.ReadAt);
        Assert.Equal("Stock request approved", alert.Title);
        Assert.Equal(
            $"IMS approved 8 of 12 requested units of Ube Halaya (Pouch · 250g) for request {created.RequestId} at TriNoma Bazaar. " +
            "You'll get another alert when the delivery arrives.",
            alert.Body);
        Assert.Equal($"/stock-management?tab=requests&locationId=8&requestId={created.RequestId}", alert.LinkPath);

        var feed = new StaffAlertFeed(f.Pos.Db);
        Assert.Equal(1, await feed.UnreadCountAsync(StockViewer(Manager)));
        Assert.Equal(0, await feed.UnreadCountAsync(StockViewer(Manager, StaffAlertAccess.OrderManagementModule))); // no Stock Management
        Assert.Equal(0, await feed.UnreadCountAsync(StockViewer(OtherBranchManager)));
        var colleague = PosHarness.Staff("portal-user-43", "Lito Santos", "Manager", 8);
        Assert.Equal(0, await feed.UnreadCountAsync(StockViewer(colleague))); // same branch, not the requester
    }

    [Fact]
    public async Task A_full_approval_says_all_units_were_approved()
    {
        using var f = new Fixture();
        var created = await f.Request(6);

        await f.Approve(created.RequestId, 6);

        Assert.StartsWith("IMS approved all 6 requested units of", Assert.Single(f.Pos.Db.StaffAlerts).Body);
    }

    [Fact]
    public async Task A_rejection_alerts_the_requester_with_the_IMS_remarks_once()
    {
        using var f = new Fixture();
        var created = await f.Request();

        var controller = f.Controller(Ims);
        await controller.Decide(created.RequestId, Decision("Rejected", remarks: "No stock is available at the warehouse."));
        await controller.Decide(created.RequestId, Decision("Rejected", remarks: "No stock is available at the warehouse."));

        var alert = Assert.Single(f.Pos.Db.StaffAlerts);
        Assert.Equal(StaffAlertKinds.StockRequestRejected, alert.Kind);
        Assert.Equal(Manager.PortalUserId, alert.RecipientPortalUserId);
        Assert.Equal("Stock request rejected", alert.Title);
        Assert.Equal(
            $"IMS rejected request {created.RequestId} for Ube Halaya (Pouch · 250g) at TriNoma Bazaar. Reason: No stock is available at the warehouse.",
            alert.Body);
    }

    [Fact]
    public async Task Long_remarks_are_shortened_to_fit_the_alert()
    {
        using var f = new Fixture();
        var created = await f.Request();

        await f.Controller(Ims).Decide(created.RequestId, Decision("Rejected", remarks: new string('x', StockRequestService.MaxNotesLength)));

        var body = Assert.Single(f.Pos.Db.StaffAlerts).Body;
        Assert.Equal(StaffAlertText.MaxBodyLength, body.Length);
        Assert.EndsWith("…", body);
    }

    [Fact]
    public async Task Each_request_for_the_same_item_gets_its_own_alert()
    {
        using var f = new Fixture();
        var first = await f.Request(4);
        var second = await f.Request(5);

        await f.Approve(first.RequestId, 4);
        await f.Controller(Ims).Decide(second.RequestId, Decision("Rejected", remarks: "Already covered by the first one."));

        var alerts = f.Pos.Db.StaffAlerts.OrderBy(a => a.Id).ToList();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(first.RequestId, alerts[0].Body);
        Assert.Contains(first.RequestId, alerts[0].LinkPath);
        Assert.Contains(second.RequestId, alerts[1].Body);
        Assert.Contains(second.RequestId, alerts[1].LinkPath);
    }

    [Fact]
    public async Task The_delivery_still_raises_its_own_alert_after_the_approval()
    {
        using var f = new Fixture();
        var created = await f.Request();
        await f.Approve(created.RequestId, 4);

        await f.Transfers().ReceiveTransferDetails(Manifest(created.RequestId), f.Service);

        var kinds = f.Pos.Db.StaffAlerts.OrderBy(a => a.Id).Select(a => a.Kind).ToList();
        Assert.Equal([StaffAlertKinds.StockRequestApproved, StaffAlertKinds.TransferArrived], kinds);
    }

    [Fact]
    public async Task A_legacy_username_requester_is_mapped_to_their_Portal_ID()
    {
        using var f = new Fixture();
        var created = await f.Request();
        var saved = f.Pos.Db.StockRequests.Single();
        saved.RequestedBy = "mreyes";
        f.Pos.Db.PosStaffUsers.Add(new PosStaffUser { AuthUserId = Manager.PortalUserId!, Username = "mreyes", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        await f.Pos.Db.SaveChangesAsync();

        await f.Approve(created.RequestId, 6);

        Assert.Equal(Manager.PortalUserId, Assert.Single(f.Pos.Db.StaffAlerts).RecipientPortalUserId);
    }

    [Fact]
    public async Task An_IMS_retry_makes_one_Portal_copy_for_the_requester()
    {
        using var f = new Fixture();
        var gateway = new FakeStaffAlertGateway();
        gateway.Grant(Manager.PortalUserId!, "Manager", StaffAlertAccess.StockManagementModule);
        gateway.Grant(OtherBranchManager.PortalUserId!, "Manager", StaffAlertAccess.StockManagementModule);
        var options = new StaffAlertOptions
        {
            Requested = true,
            GatewayUrl = "https://gateway.test",
            CallerKey = "caller-key",
            PortalUrl = "https://portal.test",
            PortalSystemSecret = "secret",
            WebOrigin = "https://pos.test",
        };
        var created = await f.Request(12);

        var controller = f.Controller(Ims);
        await controller.Decide(created.RequestId, Decision("Approved", 8));
        await new StaffAlertDispatcher(f.Pos.Db, gateway, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<StaffAlertDispatcher>.Instance).RunOnceAsync();
        await controller.Decide(created.RequestId, Decision("Approved", 8)); // retry after the copy went out
        await new StaffAlertDispatcher(f.Pos.Db, gateway, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<StaffAlertDispatcher>.Instance).RunOnceAsync();

        var copy = Assert.Single(gateway.Copies);
        Assert.Equal(Manager.PortalUserId, copy.UserId);
        Assert.Contains("8 of 12", copy.Request.Body);
    }

    // ── Sign-in routing ──

    [Theory]
    [InlineData("PUT", "/api-pos/stock-requests/SR-000001/status", true)]
    [InlineData("PUT", "/api-pos/stock-requests/SR-000001/cancel", false)] // staff only, never the IMS key
    [InlineData("PUT", "/api-pos/stock-requests/SR-000001/status/extra", false)]
    [InlineData("POST", "/api-pos/stock-requests", false)]
    [InlineData("GET", "/api-pos/stock-requests", false)]
    public void Only_the_status_route_accepts_the_IMS_service_key(string method, string path, bool expected)
    {
        Assert.Equal(expected, ScmsServiceAuth.IsServiceRoute(new PathString(path), method));
    }
}
