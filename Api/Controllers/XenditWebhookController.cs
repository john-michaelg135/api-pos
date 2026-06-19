using Microsoft.AspNetCore.Mvc;
using Api.Contracts.OrderEntry;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Domains.Entities;
using Applications.Interfaces;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/webhooks/xendit")]
public class XenditWebhookController : ControllerBase
{
    private readonly PosDbContext _db;
    private readonly ILogger<XenditWebhookController> _logger;
    private readonly IXenditService _xenditService;

    public XenditWebhookController(PosDbContext db, ILogger<XenditWebhookController> logger, IXenditService xenditService)
    {
        _db = db;
        _logger = logger;
        _xenditService = xenditService;
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
                
                // Allow Xendit's "Test and save" webhook verification to pass
                if (callback.ExternalId.StartsWith("invoice_", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Xendit webhook dashboard verification test succeeded.");
                    return Ok(new { message = "Webhook test endpoint verified successfully." });
                }

                return NotFound($"Order {callback.ExternalId} not found.");
            }

            if (string.Equals(order.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(order.PaymentMethod, "Cash on Delivery", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Received Xendit PAID webhook for order {OrderNumber}, but order is COD. Ignoring.", callback.ExternalId);
                return Ok();
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

            var oldStatus = order.OrderStatus;
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

            if (order.OrderStatus != oldStatus)
            {
                var history = new OrderStatusHistory
                {
                    OrderId = order.OrderId,
                    OldStatus = oldStatus,
                    NewStatus = order.OrderStatus,
                    ChangedBy = 0, // System/Xendit callback
                    Remarks = $"Payment callback from Xendit. Ref: {callback.PaymentId ?? callback.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                await _db.OrderStatusHistories.AddAsync(history);
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Order {OrderNumber} marked as Paid and {Status} via Xendit webhook", callback.ExternalId, order.OrderStatus);
        }

        return Ok();
    }

    // ──────────────────────────────────────────────────────────────────
    // EC-022: Confirm payment after Xendit redirect (local-dev / webhook fallback)
    // Called by the frontend success page to pull live invoice status from Xendit
    // and mark the order as Paid if Xendit confirms the payment.
    // POST /api-pos/webhooks/xendit/confirm-payment?orderNumber={orderNumber}
    // ──────────────────────────────────────────────────────────────────
    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment([FromQuery] string orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
            return BadRequest("orderNumber query parameter is required.");

        var order = await _db.Orders
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

        if (order == null)
            return NotFound($"Order {orderNumber} not found.");

        // If it's a COD order, it is paid on delivery, not via Xendit.
        if (string.Equals(order.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(order.PaymentMethod, "Cash on Delivery", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { confirmed = false, reason = "COD orders are paid on delivery." });
        }

        // Already marked paid — nothing to do
        if (string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            return Ok(new { confirmed = true, alreadyPaid = true });

        var status = await _xenditService.GetInvoiceStatusByOrderNumberAsync(orderNumber);
        _logger.LogInformation("ConfirmPayment: Xendit status for order {OrderNumber} is {Status}", orderNumber, status ?? "null");

        if (!string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase))
            return Ok(new { confirmed = false, xenditStatus = status });

        var pendingPayment = order.Payments
            .FirstOrDefault(p => p.PaymentStatus == "Pending");

        if (pendingPayment != null)
        {
            pendingPayment.PaymentStatus = "Paid";
            pendingPayment.PaidAt = DateTime.UtcNow;
            _db.Payments.Update(pendingPayment);
        }
        else
        {
            await _db.Payments.AddAsync(new Payment
            {
                OrderId       = order.OrderId,
                AmountPaid    = order.TotalAmount,
                PaymentChannel = "Xendit",
                PaymentStatus = "Paid",
                PaidAt        = DateTime.UtcNow
            });
        }

        order.PaymentStatus = "Paid";
        if (string.Equals(order.OrderStatus, "Pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(order.OrderStatus, "Awaiting Stock", StringComparison.OrdinalIgnoreCase))
        {
            order.OrderStatus = "Processing";
        }
        order.UpdatedAt = DateTime.UtcNow;

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        _logger.LogInformation("ConfirmPayment: Order {OrderNumber} marked as Paid via frontend confirm.", orderNumber);
        return Ok(new { confirmed = true, xenditStatus = status });
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
