using Microsoft.AspNetCore.Mvc;
using Api.Contracts.OrderEntry;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Domains.Entities;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/webhooks/xendit")]
public class XenditWebhookController : ControllerBase
{
    private readonly PosDbContext _db;
    private readonly ILogger<XenditWebhookController> _logger;

    public XenditWebhookController(PosDbContext db, ILogger<XenditWebhookController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost("invoice-paid")]
    public async Task<IActionResult> InvoicePaid([FromBody] XenditInvoiceCallbackDto callback)
    {
        _logger.LogInformation("Received Xendit webhook callback. ExternalId: {ExternalId}, Status: {Status}", callback.ExternalId, callback.Status);

        // Optional webhook callback token verification
        var expectedToken = Environment.GetEnvironmentVariable("XENDIT_CALLBACK_TOKEN");
        if (!string.IsNullOrEmpty(expectedToken))
        {
            if (!Request.Headers.TryGetValue("x-callback-token", out var headerToken) || headerToken != expectedToken)
            {
                _logger.LogWarning("Xendit webhook callback token validation failed. Header token did not match expected token.");
                return Unauthorized("Invalid callback token.");
            }
        }
        else
        {
            _logger.LogWarning("XENDIT_CALLBACK_TOKEN environment variable is not configured. Webhook security validation is bypassed.");
        }

        if (string.Equals(callback.Status, "PAID", StringComparison.OrdinalIgnoreCase))
        {
            var order = await _db.Orders
                .Include(o => o.Payments)
                .FirstOrDefaultAsync(o => o.OrderNumber == callback.ExternalId);

            if (order == null)
            {
                _logger.LogWarning("Order with number {OrderNumber} not found for Xendit webhook callback", callback.ExternalId);
                return NotFound($"Order {callback.ExternalId} not found.");
            }

            // Find if there's already a pending payment record
            var pendingPayment = order.Payments
                .FirstOrDefault(p => p.PaymentStatus == "Pending" && p.PaymentChannel == "GCash");

            if (pendingPayment != null)
            {
                pendingPayment.PaymentStatus = "Success";
                pendingPayment.GatewayReferenceNumber = callback.PaymentId ?? callback.Id;
                pendingPayment.PaidAt = callback.PaidAt ?? DateTime.UtcNow;
                _db.Payments.Update(pendingPayment);
            }
            else
            {
                // Create new successful payment record if none exists
                var payment = new Payment
                {
                    OrderId = order.OrderId,
                    AmountPaid = callback.Amount,
                    PaymentChannel = callback.PaymentChannel ?? "GCash",
                    PaymentStatus = "Success",
                    GatewayReferenceNumber = callback.PaymentId ?? callback.Id,
                    PaidAt = callback.PaidAt ?? DateTime.UtcNow
                };
                await _db.Payments.AddAsync(payment);
            }

            order.PaymentStatus = "Paid";
            if (string.Equals(order.OrderSource, "POS", StringComparison.OrdinalIgnoreCase))
            {
                order.OrderStatus = "Completed";
            }
            else
            {
                if (string.Equals(order.OrderStatus, "Shipped", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(order.OrderStatus, "ready_for_delivery", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(order.OrderStatus, "delivered", StringComparison.OrdinalIgnoreCase))
                {
                    order.OrderStatus = "Delivered";
                }
                else if (string.Equals(order.OrderStatus, "Pending", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(order.OrderStatus, "Awaiting Stock", StringComparison.OrdinalIgnoreCase))
                {
                    order.OrderStatus = "Processing";
                }
            }
            order.UpdatedAt = DateTime.UtcNow;

            _db.Orders.Update(order);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Order {OrderNumber} marked as Paid and {Status} via Xendit webhook", callback.ExternalId, order.OrderStatus);
        }

        return Ok();
    }

    // ──────────────────────────────────────────────────────────────────
    // US-EC-021: Webhook audit log — GET /api-pos/webhooks/xendit/logs
    // Returns all GCash/Xendit payment transactions for the manager dashboard.
    // ──────────────────────────────────────────────────────────────────
    [HttpGet("logs")]
    public async Task<IActionResult> GetWebhookLogs()
    {
        var payments = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Order)
            .Where(p => p.PaymentChannel == "GCash")
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                paymentId              = p.PaymentId,
                orderId                = p.OrderId,
                orderNumber            = p.Order != null ? p.Order.OrderNumber : null,
                orderSource            = p.Order != null ? p.Order.OrderSource : null,
                amountPaid             = p.AmountPaid,
                paymentChannel         = p.PaymentChannel,
                paymentStatus          = p.PaymentStatus,
                gatewayReferenceNumber = p.GatewayReferenceNumber,
                paidAt                 = p.PaidAt
            })
            .ToListAsync();

        return Ok(payments);
    }
}
