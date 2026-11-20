using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Applications.StaffAlerts;

namespace Infrastructures.Externals;

/// <summary>
/// Portal recipient directory (<c>POST {PORTAL_URL}/api/integrations/recipients</c>, Basic auth as the
/// POS System) and the notification service through the gateway as its "pos" Caller
/// (<c>{POS_GATEWAY_URL}/notifications/notifications…</c> and <c>/live-tickets</c>).
/// </summary>
public sealed class StaffAlertGatewayClient : IStaffAlertGateway
{
    public const string HttpClientName = "StaffAlertGateway";
    private const string PortalApp = "Portal";
    private const string NudgeTitle = "POS staff feed changed";
    private const string NudgeBody = "Refresh the POS staff feed.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StaffAlertOptions _options;

    public StaffAlertGatewayClient(IHttpClientFactory httpClientFactory, StaffAlertOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<PortalRecipient>> RecipientsAsync(CancellationToken cancellationToken)
    {
        if (!_options.HasDirectory) throw new StaffAlertGatewayException("directory_not_configured");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.PortalUrl}/api/integrations/recipients");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.PortalSystemName}:{_options.PortalSystemSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var body = await SendAsync<RecipientsResponse>(request, "directory", cancellationToken);
        if (body?.Staff is null) throw new StaffAlertGatewayException("directory_invalid_response");
        return body.Staff
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => new PortalRecipient(s.Id!, s.Username, s.Name, s.Role, s.Modules ?? []))
            .ToList();
    }

    public async Task<RemoteCopy> SendAsync(PortalCopyRequest copy, CancellationToken cancellationToken)
    {
        var created = await PostNotificationAsync(new SendBody(
            copy.IdempotencyKey, copy.Type, copy.Title, copy.Body, copy.Link,
            _options.SendingApp, PortalApp, [copy.UserId]), cancellationToken);
        // A replay stores nothing new and returns an empty list: recover the original by its key.
        var remote = created?.Notifications?.FirstOrDefault(n => n.UserId == copy.UserId) is { } first
            ? ToCopy(first)
            : await FindAsync(copy.UserId, copy.IdempotencyKey, cancellationToken);
        return remote ?? throw new StaffAlertGatewayException("copy_not_returned");
    }

    public Task<RemoteCopy?> FindAsync(string userId, string idempotencyKey, CancellationToken cancellationToken) =>
        LookupAsync(userId, new() { ["idempotencyKey"] = idempotencyKey, ["sendingApp"] = _options.SendingApp }, cancellationToken);

    public Task<RemoteCopy?> StatusAsync(string userId, Guid id, CancellationToken cancellationToken) =>
        LookupAsync(userId, new() { ["id"] = id.ToString() }, cancellationToken);

    public async Task MarkReadAsync(string userId, Guid id, CancellationToken cancellationToken)
    {
        using var request = GatewayRequest(HttpMethod.Post, $"/notifications/notifications/{id}/read");
        request.Content = JsonContent.Create(new InboxBody(userId, PortalApp));
        // 404: the copy no longer exists, so there is nothing left to mark.
        await SendAsync<object>(request, "mark_read", cancellationToken, allowNotFound: true);
    }

    public async Task NudgeAsync(CancellationToken cancellationToken) =>
        await PostNotificationAsync(new SendBody(
            null, StaffAlertKinds.StaffChanged, NudgeTitle, NudgeBody, null,
            _options.SendingApp, _options.SendingApp, [_options.StaffChannel]), cancellationToken);

    public async Task<string> LiveUrlAsync(CancellationToken cancellationToken)
    {
        using var request = GatewayRequest(HttpMethod.Post, "/live-tickets");
        request.Content = JsonContent.Create(new TicketBody(_options.StaffChannel));
        var body = await SendAsync<TicketResponse>(request, "live_ticket", cancellationToken);
        if (string.IsNullOrWhiteSpace(body?.Ticket)) throw new StaffAlertGatewayException("live_ticket_missing");
        var socketBase = _options.GatewayUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + _options.GatewayUrl["https://".Length..]
            : "ws://" + _options.GatewayUrl!["http://".Length..];
        return $"{socketBase}/notifications/live?ticket={Uri.EscapeDataString(body.Ticket)}";
    }

    private async Task<SendResponse?> PostNotificationAsync(SendBody body, CancellationToken cancellationToken)
    {
        using var request = GatewayRequest(HttpMethod.Post, "/notifications/notifications");
        request.Content = JsonContent.Create(body);
        return await SendAsync<SendResponse>(request, "send", cancellationToken);
    }

    private async Task<RemoteCopy?> LookupAsync(string userId, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        query["userId"] = userId;
        query["app"] = PortalApp;
        var queryString = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        using var request = GatewayRequest(HttpMethod.Get, $"/notifications/notifications/lookup?{queryString}");
        var body = await SendAsync<LookupResponse>(request, "lookup", cancellationToken);
        return body?.Notification is { } found ? ToCopy(found) : null;
    }

    private HttpRequestMessage GatewayRequest(HttpMethod method, string path)
    {
        if (!_options.HasGateway) throw new StaffAlertGatewayException("gateway_not_configured");
        var request = new HttpRequestMessage(method, $"{_options.GatewayUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CallerKey);
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, string operation, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, deadline.Token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
                throw new StaffAlertGatewayException($"{operation}_http_{(int)response.StatusCode}");
            if (typeof(T) == typeof(object)) return default;
            return await response.Content.ReadFromJsonAsync<T>(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StaffAlertGatewayException($"{operation}_timeout", transport: true);
        }
        catch (HttpRequestException)
        {
            throw new StaffAlertGatewayException($"{operation}_network", transport: true);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new StaffAlertGatewayException($"{operation}_invalid_response");
        }
    }

    private static RemoteCopy? ToCopy(RemoteNotification n) =>
        Guid.TryParse(n.Id, out var id) ? new RemoteCopy(id, n.ReadAt is not null) : null;

    private sealed record SendBody(
        [property: JsonPropertyName("idempotencyKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IdempotencyKey,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("link"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link,
        [property: JsonPropertyName("sendingApp")] string SendingApp,
        [property: JsonPropertyName("targetApp")] string TargetApp,
        [property: JsonPropertyName("userIds")] string[] UserIds);

    private sealed record InboxBody(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("app")] string App);

    private sealed record TicketBody([property: JsonPropertyName("userId")] string UserId);

    private sealed record TicketResponse([property: JsonPropertyName("ticket")] string? Ticket);

    private sealed record RemoteNotification(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("readAt")] string? ReadAt);

    private sealed record SendResponse(
        [property: JsonPropertyName("notifications")] List<RemoteNotification>? Notifications,
        [property: JsonPropertyName("stored")] int Stored,
        [property: JsonPropertyName("connected")] int Connected);

    private sealed record LookupResponse([property: JsonPropertyName("notification")] RemoteNotification? Notification);

    private sealed record RecipientsResponse([property: JsonPropertyName("staff")] List<RecipientDto>? Staff);

    private sealed record RecipientDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("modules")] List<string>? Modules);
}
