using Applications.Audit;
using Applications.Interfaces;
using Api.Contracts.Refund;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;

namespace Applications.Services;

public class RefundService : IRefundService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IRefundNotificationService _refundNotificationService;
    private readonly IAuditLogService _auditLogService;

    public RefundService(PosDbContext db, IInventoryService inventoryService, IHttpContextAccessor httpContextAccessor, IRefundNotificationService refundNotificationService, IAuditLogService auditLogService)
    {
        _db = db;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _refundNotificationService = refundNotificationService;
        _auditLogService = auditLogService;
    }

    // ────────────────────────────────────────────────────
    // US-POS-016: Submit refund request — always saved as "Pending"
    // ────────────────────────────────────────────────────

    public async Task<RefundResponseDto> SubmitRefundAsync(CreateRefundRequestDto dto)
    {
        // Validate the referenced order exists
        var order = await _db.Orders.FindAsync(dto.OrderId);
        if (order == null)
            throw new InvalidOperationException($"Order with ID {dto.OrderId} not found.");

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            if (dto.LocationId != currentUser.LocationId)
                throw new InvalidOperationException("You are not authorized to submit refunds for other locations.");
            if (order.LocationId != currentUser.LocationId)
                throw new InvalidOperationException("You are not authorized to submit refunds for orders of other locations.");
        }

        // Validate the variation exists
        var variation = await _db.ProductVariations.FindAsync(dto.VariationId);
        if (variation == null)
            throw new InvalidOperationException($"Product variation with ID {dto.VariationId} not found.");

        if (dto.QuantityToReturn <= 0)
            throw new InvalidOperationException("Quantity to return must be greater than zero.");

        var now = DateTime.UtcNow;

        // US-POS-016: Status is hardcoded to "Pending" — manager must review before any action
        var refund = new RefundRequest
        {
            OrderId         = dto.OrderId,
            VariationId     = dto.VariationId,
            LocationId      = dto.LocationId,
            QuantityToReturn = dto.QuantityToReturn,
            Reason          = dto.Reason,
            RefundMethod    = RefundMethods.For(order.PaymentMethod),
            Status          = "Pending",
            RequestedBy     = dto.RequestedBy,
            CreatedAt       = now,
            UpdatedAt       = now
        };

        await _db.RefundRequests.AddAsync(refund);
        await _db.SaveChangesAsync();

        await RecordRefundAsync(refund, order.OrderNumber, AuditActions.Created, "refund_requested",
            "requested a refund", refund.CreatedAt, $"refund-request:{refund.RefundRequestId}", previousStatus: null);

        return MapToResponse(refund);
    }

    // ────────────────────────────────────────────────────
    // US-POS-017: Approve refund — restores stock and logs manager
    // ────────────────────────────────────────────────────

    public async Task<RefundResponseDto?> ApproveRefundAsync(int refundRequestId, ApproveRefundDto dto)
    {
        var refund = await _db.RefundRequests.FindAsync(refundRequestId);
        if (refund == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && refund.LocationId != currentUser.LocationId)
        {
            throw new InvalidOperationException("You are not authorized to approve refunds for other locations.");
        }

        if (refund.Status != "Pending")
            throw new InvalidOperationException($"Refund #{refundRequestId} is already '{refund.Status}'. Only Pending refunds can be approved.");

        var now = DateTime.UtcNow;

        var isUnsellable = refund.Reason.Contains("(Unsellable)", StringComparison.OrdinalIgnoreCase) ||
            refund.Reason.StartsWith("Damaged/Expired", StringComparison.OrdinalIgnoreCase);

        if (isUnsellable)
        {
            await _inventoryService.RecordStockLossAsync(
                refund.VariationId,
                refund.LocationId,
                refund.QuantityToReturn,
                refund.OrderId,
                $"Refund #{refundRequestId}",
                refund.Reason);
        }
        else
        {
            await _inventoryService.RestoreStockAsync(
                refund.VariationId,
                refund.LocationId,
                refund.QuantityToReturn,
                refund.OrderId,
                "Refund",
                $"Refund #{refundRequestId}");
        }

        // US-POS-017: Log the exact manager who approved
        refund.Status     = "Approved";
        refund.ApprovedBy = dto.ApprovedBy;
        refund.ApprovedAt = now;
        refund.UpdatedAt  = now;

        _db.RefundRequests.Update(refund);
        await _db.SaveChangesAsync();

        var response = MapToResponse(refund);
        
        // US-POS-025: Push live notification to cashiers at that location
        _ = _refundNotificationService.NotifyRefundApprovedAsync(refund.LocationId, response);

        await RecordRefundAsync(refund, await OrderNumberAsync(refund.OrderId), AuditActions.Updated, "refund_approved",
            "approved", now, $"refund-request-approved:{refund.RefundRequestId}", previousStatus: "Pending");

        return response;
    }

    // ────────────────────────────────────────────────────
    // Reject refund — logs manager and sets status to Rejected
    // ────────────────────────────────────────────────────

    public async Task<RefundResponseDto?> RejectRefundAsync(int refundRequestId, ApproveRefundDto dto)
    {
        var refund = await _db.RefundRequests.FindAsync(refundRequestId);
        if (refund == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && refund.LocationId != currentUser.LocationId)
        {
            throw new InvalidOperationException("You are not authorized to reject refunds for other locations.");
        }

        if (refund.Status != "Pending")
            throw new InvalidOperationException($"Refund #{refundRequestId} is already '{refund.Status}'. Only Pending refunds can be rejected.");

        var now = DateTime.UtcNow;

        refund.Status     = "Rejected";
        refund.ApprovedBy = dto.ApprovedBy;
        refund.ApprovedAt = now;
        refund.UpdatedAt  = now;

        _db.RefundRequests.Update(refund);
        await _db.SaveChangesAsync();

        await RecordRefundAsync(refund, await OrderNumberAsync(refund.OrderId), AuditActions.Updated, "refund_rejected",
            "rejected", now, $"refund-request-rejected:{refund.RefundRequestId}", previousStatus: "Pending");

        return MapToResponse(refund);
    }


    // ────────────────────────────────────────────────────
    // Supporting: List all refund requests
    // ────────────────────────────────────────────────────

    public async Task<List<RefundResponseDto>> GetAllRefundsAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.RefundRequests.AsQueryable();

        if (currentUser?.SubRole == "Cashier")
        {
            var locationId = currentUser.LocationId ?? 0;
            query = query.Where(r => r.LocationId == locationId);
        }

        var refunds = await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return refunds.Select(MapToResponse).ToList();
    }

    // Per-item refund API: the target is the refund request, filed under its order's number.
    private async Task RecordRefundAsync(
        RefundRequest refund, string? orderNumber, string action, string operation, string verb,
        DateTime occurredAt, string operationId, string? previousStatus)
    {
        var label = $"Refund request #{refund.RefundRequestId}";
        var branch = await _db.Locations.AsNoTracking()
            .Where(l => l.LocationId == refund.LocationId).Select(l => l.LocationName).FirstOrDefaultAsync();
        var onOrder = orderNumber is null ? string.Empty : $" for Order {orderNumber}";
        var what = action == AuditActions.Created ? $"{verb}{onOrder}" : $"{verb} {label.ToLowerInvariant()}{onOrder}";
        await _auditLogService.RecordAsync(new AuditEvent
        {
            Module = PosModules.OrderManagement,
            Action = action,
            Target = new AuditTarget("refund_request", refund.RefundRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), label),
            Describe = who => $"{who} {what}{OrderAudit.AtBranch(branch, refund.LocationId)}.",
            OccurredAt = occurredAt,
            OperationId = operationId,
            Metadata = new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["order_id"] = refund.OrderId,
                ["location_id"] = refund.LocationId,
                ["variation_id"] = refund.VariationId,
                ["quantity"] = refund.QuantityToReturn,
                ["previous_status"] = previousStatus,
                ["new_status"] = refund.Status,
            },
        });
    }

    private async Task<string?> OrderNumberAsync(int orderId) =>
        await _db.Orders.AsNoTracking().Where(o => o.OrderId == orderId).Select(o => o.OrderNumber).FirstOrDefaultAsync();

    private static RefundResponseDto MapToResponse(RefundRequest r) => new()
    {
        RefundRequestId = r.RefundRequestId,
        OrderId         = r.OrderId,
        VariationId     = r.VariationId,
        LocationId      = r.LocationId,
        QuantityToReturn = r.QuantityToReturn,
        Reason          = r.Reason,
        Status          = r.Status,
        RequestedBy     = r.RequestedBy,
        ApprovedBy      = r.ApprovedBy,
        ApprovedAt      = r.ApprovedAt,
        CreatedAt       = r.CreatedAt,
        UpdatedAt       = r.UpdatedAt
    };
}
