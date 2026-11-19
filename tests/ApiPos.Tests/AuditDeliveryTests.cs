using System.Net;
using System.Text.Json;
using Api.Contracts.Shared;
using Applications.Audit;
using Infrastructures.Externals;

namespace ApiPos.Tests;

/// <summary>The gateway adapter and the boundary rules: path, credential, body, deadline and failures.</summary>
public class AuditDeliveryTests
{
    private static readonly AuditDeliveryOptions Options = new()
    {
        Requested = true,
        GatewayUrl = "https://gateway.test",
        CallerKey = "pos-caller-key",
        Timeout = TimeSpan.FromMilliseconds(200),
    };

    private static AuditLogEntry Entry() => new()
    {
        Module = "Order Management",
        Action = AuditActions.Updated,
        Outcome = "success",
        Actor = new AuditIdentity("user", "portal-user-42", "Mira Reyes"),
        Target = new AuditIdentity("order", "4821", "Order #4821"),
        Description = "Mira Reyes approved a partial refund for Order #4821 at Branch 8.",
        OccurredAt = "2026-11-19T03:14:00.000Z",
        Metadata = new Dictionary<string, object?> { ["operation"] = "refund_approved", ["location_id"] = 8, ["partial"] = true },
        IdempotencyKey = AuditLogEntry.IdempotencyKeyFor(AuditActions.Updated, "order", "4821", "order-history:7314"),
    };

    [Fact]
    public void Idempotency_key_matches_the_issue_example_and_CRMS()
    {
        Assert.Equal(
            "2476ac769028b26d1635099fc4e4676750def469de725fe3d79dd6c8d80f2833",
            AuditLogEntry.IdempotencyKeyFor("record.updated", "order", "4821", "order-history:7314"));
        Assert.NotEqual(
            AuditLogEntry.IdempotencyKeyFor("record.updated", "order", "4821", "order-history:7314"),
            AuditLogEntry.IdempotencyKeyFor("record.updated", "order", "4821", "order-history:7315"));
    }

    [Fact]
    public async Task Posts_snake_case_json_to_the_gateway_with_the_caller_key()
    {
        var handler = StubHandler.Status(HttpStatusCode.Created, """{"id":"8d7c"}""");
        var result = await new AuditLogClient(new StubHttpClientFactory(handler), Options).WriteAsync(Entry());

        Assert.True(result.Delivered);
        Assert.Equal("8d7c", result.EventId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://gateway.test/audit/events", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer pos-caller-key", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("POS", root.GetProperty("source").GetString());
        Assert.Equal("portal-user-42", root.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("Mira Reyes", root.GetProperty("actor").GetProperty("display_name").GetString());
        Assert.Equal("2026-11-19T03:14:00.000Z", root.GetProperty("occurred_at").GetString());
        Assert.Equal("2476ac769028b26d1635099fc4e4676750def469de725fe3d79dd6c8d80f2833", root.GetProperty("idempotency_key").GetString());
        Assert.True(root.GetProperty("metadata").GetProperty("partial").GetBoolean());
        Assert.False(root.TryGetProperty("request_id", out _)); // left out when absent
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "payload_rejected")]
    [InlineData(HttpStatusCode.Unauthorized, "credential_rejected")]
    [InlineData(HttpStatusCode.Forbidden, "credential_rejected")]
    [InlineData(HttpStatusCode.TooManyRequests, "throttled")]
    [InlineData(HttpStatusCode.BadGateway, "gateway_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "gateway_unavailable")]
    public async Task Refused_writes_report_their_category(HttpStatusCode status, string category)
    {
        var result = await new AuditLogClient(new StubHttpClientFactory(StubHandler.Status(status)), Options).WriteAsync(Entry());
        Assert.Equal(category, result.Category);
        Assert.Equal((int)status, result.StatusCode);
    }

    [Fact]
    public async Task A_slow_gateway_is_cut_off_at_the_deadline()
    {
        var slow = new StubHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var started = DateTime.UtcNow;
        var result = await new AuditLogClient(new StubHttpClientFactory(slow), Options).WriteAsync(Entry());

        Assert.Equal("timeout", result.Category);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Default_deadline_is_five_seconds() => Assert.Equal(TimeSpan.FromSeconds(5), new AuditDeliveryOptions().Timeout);

    [Fact]
    public async Task A_network_error_is_reported_not_thrown()
    {
        var broken = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var result = await new AuditLogClient(new StubHttpClientFactory(broken), Options).WriteAsync(Entry());
        Assert.Equal("network_error", result.Category);
    }

    [Fact]
    public void Incomplete_settings_keep_delivery_off()
    {
        var options = AuditDeliveryOptions.FromEnvironment(name => name == AuditDeliveryOptions.EnabledVariable ? "true" : null);
        Assert.True(options.Requested);
        Assert.False(options.Enabled);
    }

    private static AuditEvent Event(Dictionary<string, object?>? metadata = null, string? operationId = "op-1") => new()
    {
        Module = "Order Management",
        Action = AuditActions.Updated,
        Target = new AuditTarget("order", "1", "Order ORD-1"),
        Describe = who => $"{who} did a thing.",
        OccurredAt = new DateTime(2026, 11, 19, 3, 14, 0, DateTimeKind.Utc),
        OperationId = operationId,
        Metadata = metadata ?? new() { ["operation"] = "thing" },
    };

    [Fact]
    public async Task Disabled_means_zero_writes()
    {
        using var pos = new PosHarness(enabled: false);
        pos.SignIn(PosHarness.Staff("portal-1", "Ana Cruz", "Cashier", 8));
        await pos.Audit.RecordAsync(Event());
        Assert.Empty(pos.Writer.Entries);
    }

    [Fact]
    public async Task A_throwing_writer_is_caught_and_logged_without_credentials()
    {
        using var pos = new PosHarness();
        pos.SignIn(PosHarness.Staff("portal-1", "Ana Cruz", "Cashier", 8));
        pos.Writer.Throw = new InvalidOperationException("boom with secret pos-caller-key");

        await pos.Audit.RecordAsync(Event()); // must not throw

        var message = Assert.Single(pos.AuditLogger.Messages);
        Assert.Contains("writer_error:InvalidOperationException", message);
        Assert.Contains("order:1", message);
        Assert.DoesNotContain("pos-caller-key", message);
    }

    [Fact]
    public async Task An_unverified_staff_actor_skips_the_event()
    {
        using var pos = new PosHarness();
        // A user without a Portal-validated id (e.g. parsed from an unverified bearer token).
        pos.SignIn(new Api.Middlewares.CurrentUserContext { Username = "someone", DisplayName = "Someone" });

        await pos.Audit.RecordAsync(Event());

        Assert.Empty(pos.Writer.Entries);
        Assert.Contains("no verified staff actor", Assert.Single(pos.AuditLogger.Messages));
    }

    [Fact]
    public async Task Username_stands_in_for_a_missing_display_name()
    {
        using var pos = new PosHarness();
        var user = PosHarness.Staff("portal-1", "", "Cashier", 8);
        user.DisplayName = null;
        user.Username = "acruz";
        pos.SignIn(user);

        await pos.Audit.RecordAsync(Event());

        Assert.Equal("acruz", Assert.Single(pos.Writer.Entries).Actor.DisplayName);
    }

    [Fact]
    public async Task Metadata_outside_the_allowlist_or_over_the_limit_is_left_out()
    {
        using var pos = new PosHarness();
        pos.SignIn(PosHarness.Staff("portal-1", "Ana Cruz", "Cashier", 8));

        await pos.Audit.RecordAsync(Event(new() { ["operation"] = "thing", ["customer_phone"] = "0917", ["location_id"] = 8 }));
        await pos.Audit.RecordAsync(Event(new() { ["operation"] = "thing", ["changed_fields"] = new string('x', 5000) }));

        Assert.Equal(["operation", "location_id"], pos.Writer.Entries[0].Metadata!.Keys);
        Assert.Equal(["operation"], pos.Writer.Entries[1].Metadata!.Keys);
        Assert.True(pos.Writer.Entries.All(e => e.ToUtf8Json().Length <= AuditLogEntry.MaxBodyBytes));
    }

    [Fact]
    public async Task An_edit_retried_with_the_same_Idempotency_Key_keeps_its_key_and_new_edits_differ()
    {
        using var pos = new PosHarness();
        pos.SignIn(PosHarness.Staff("portal-1", "Ana Cruz", "Manager", 8));
        pos.Accessor.HttpContext!.Request.Headers["Idempotency-Key"] = "edit-123";

        await pos.Audit.RecordAsync(Event(operationId: null));
        await pos.Audit.RecordAsync(Event(operationId: null));
        pos.Accessor.HttpContext.Request.Headers.Remove("Idempotency-Key");
        await pos.Audit.RecordAsync(Event(operationId: null));
        await pos.Audit.RecordAsync(Event(operationId: null));

        var keys = pos.Writer.Entries.Select(e => e.IdempotencyKey).ToArray();
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(3, keys.Distinct().Count());
        Assert.Equal(pos.Accessor.HttpContext.TraceIdentifier, pos.Writer.Entries[0].RequestId);
    }
}
