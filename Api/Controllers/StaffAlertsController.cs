using Api.Middlewares;
using Applications.StaffAlerts;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// The POS staff bell. PortalSessionMiddleware validates the pos_session against every bell Module,
/// so a stock-only user sees stock alerts and an order-only user sees order alerts. The viewer is
/// always the validated session, never a request parameter.
/// </summary>
[ApiController]
[Route("api-pos/notifications")]
public class StaffAlertsController : ControllerBase
{
    private readonly StaffAlertFeed _feed;

    public StaffAlertsController(StaffAlertFeed feed)
    {
        _feed = feed;
    }

    // GET api-pos/notifications?cursor=&limit=&unread=true
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, [FromQuery] bool unread = false, CancellationToken cancellationToken = default)
    {
        if (Viewer() is not { } viewer) return SessionRequired();
        NoStore();
        var page = await _feed.ListAsync(viewer, cursor, limit, unread, cancellationToken);
        return Ok(new { items = page.Items, nextCursor = page.NextCursor, unread = page.Unread });
    }

    // GET api-pos/notifications/unread-count
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer) return SessionRequired();
        NoStore();
        return Ok(new { unread = await _feed.UnreadCountAsync(viewer, cancellationToken) });
    }

    // POST api-pos/notifications/{id}/read
    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer) return SessionRequired();
        NoStore();
        if (!await _feed.MarkReadAsync(viewer, id, cancellationToken))
            return NotFound(new { message = "Notification not found." });
        return Ok(new { id, read = true, unread = await _feed.UnreadCountAsync(viewer, cancellationToken) });
    }

    // POST api-pos/notifications/read-all
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (Viewer() is not { } viewer) return SessionRequired();
        NoStore();
        var updated = await _feed.MarkAllReadAsync(viewer, cancellationToken);
        return Ok(new { updated, unread = await _feed.UnreadCountAsync(viewer, cancellationToken) });
    }

    // POST api-pos/notifications/live-ticket — short-lived socket address for the POS staff channel.
    [HttpPost("live-ticket")]
    public async Task<IActionResult> LiveTicket([FromServices] IStaffAlertGateway gateway, [FromServices] StaffAlertOptions options,
        [FromServices] ILogger<StaffAlertsController> logger, CancellationToken cancellationToken)
    {
        if (Viewer() is null) return SessionRequired();
        NoStore();
        if (!options.LiveEnabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Live updates are not configured; the bell will poll." });
        try
        {
            // The ticket is a credential: it goes to this browser only and is never logged.
            return Ok(new { url = await gateway.LiveUrlAsync(cancellationToken) });
        }
        catch (StaffAlertGatewayException ex)
        {
            logger.LogWarning("POS live ticket unavailable ({Category}).", ex.Category);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Live updates are temporarily unavailable; the bell will poll." });
        }
    }

    private StaffViewer? Viewer()
    {
        var user = HttpContext.GetCurrentUser();
        if (user is null || string.IsNullOrWhiteSpace(user.PortalUserId)) return null;
        return new StaffViewer(user.PortalUserId, user.Role, user.PortalModules, user.LocationIds);
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private IActionResult SessionRequired() =>
        Unauthorized(new { message = "A valid POS session is required." });
}
