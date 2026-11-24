using System.Globalization;
using Api.Middlewares;
using Domains.Entities;

namespace Applications.Audit;

/// <summary>Stock Management events, shared by the services that save receipts and adjustments.</summary>
public static class StockAudit
{
    public static AuditEvent ReceiptRecorded(StockReceiving receiving, string item, string? branch, AuditActor? actor = null) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Created,
        Actor = actor,
        Target = new AuditTarget("stock_receipt", Id(receiving.ReceivingId), $"Stock receipt #{receiving.ReceivingId}"),
        Describe = who => $"{who} received {receiving.QuantityReceived} × {item}{OrderAudit.AtBranch(branch, receiving.LocationId)}.",
        OccurredAt = receiving.ReceivedAt,
        OperationId = $"stock-receipt:{receiving.ReceivingId}",
        Metadata = new Dictionary<string, object?>
        {
            ["operation"] = "stock_received",
            ["location_id"] = receiving.LocationId,
            ["variation_id"] = receiving.VariationId,
            ["quantity"] = receiving.QuantityReceived,
        },
    };

    public static AuditEvent AdjustmentSubmitted(StockAdjustment adjustment, string item, string? branch) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Created,
        Target = AdjustmentTarget(adjustment),
        Describe = who => $"{who} submitted a {adjustment.AdjustmentType.ToLowerInvariant()} adjustment of {adjustment.Quantity:+#;-#;0} × {item}{OrderAudit.AtBranch(branch, adjustment.LocationId)}.",
        OccurredAt = adjustment.CreatedAt,
        OperationId = $"stock-adjustment:{adjustment.AdjustmentId}",
        Metadata = AdjustmentMetadata(adjustment, "adjustment_submitted", previousStatus: null),
    };

    /// <summary>An adjustment is approved once (the status gate refuses a second), so the id is its identity.</summary>
    public static AuditEvent AdjustmentApproved(StockAdjustment adjustment, string item, string? branch, string previousStatus) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Updated,
        Target = AdjustmentTarget(adjustment),
        Describe = who => $"{who} approved stock adjustment #{adjustment.AdjustmentId} ({adjustment.Quantity:+#;-#;0} × {item}){OrderAudit.AtBranch(branch, adjustment.LocationId)}.",
        OccurredAt = adjustment.ApprovedAt ?? DateTime.UtcNow,
        OperationId = $"stock-adjustment-approved:{adjustment.AdjustmentId}",
        Metadata = AdjustmentMetadata(adjustment, "adjustment_approved", previousStatus),
    };

    /// <summary>A manager wrote off unsellable units; recorded once, so the id is its identity.</summary>
    public static AuditEvent LossRecorded(StockAdjustment loss, string item, string? branch) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Created,
        Target = new AuditTarget("stock_loss", Id(loss.AdjustmentId), $"Stock loss #{loss.AdjustmentId}"),
        Describe = who => $"{who} recorded a system loss of {Math.Abs(loss.Quantity)} × {item} ({(loss.LossReason ?? "Other").ToLowerInvariant()}){OrderAudit.AtBranch(branch, loss.LocationId)}.",
        OccurredAt = loss.CreatedAt,
        OperationId = $"stock-loss:{loss.AdjustmentId}",
        Metadata = new Dictionary<string, object?>
        {
            ["operation"] = "stock_loss_recorded",
            ["location_id"] = loss.LocationId,
            ["variation_id"] = loss.VariationId,
            ["quantity"] = Math.Abs(loss.Quantity),
            ["loss_reason"] = loss.LossReason,
            ["unit_price"] = loss.UnitPrice,
        },
    };

    /// <summary>A branch asked IMS for stock; created once, so the request id is its identity.</summary>
    public static AuditEvent StockRequested(StockRequest request, string item, string? branch) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Created,
        Target = RequestTarget(request),
        Describe = who => $"{who} requested {request.Quantity} × {item} from IMS{OrderAudit.AtBranch(branch, request.LocationId)} ({request.RequestId}).",
        OccurredAt = request.RequestedAt,
        OperationId = $"stock-request:{request.RequestId}",
        Metadata = RequestMetadata(request, "stock_requested", previousStatus: null),
    };

    /// <summary>IMS approved or rejected a request. A request is decided once (the status gate refuses a second).</summary>
    public static AuditEvent StockRequestDecided(StockRequest request, string item, string? branch, string previousStatus) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Updated,
        Actor = AuditActor.Process("ims", string.IsNullOrWhiteSpace(request.DecidedByName) ? "IMS" : $"IMS · {request.DecidedByName}"),
        Target = RequestTarget(request),
        Describe = who => request.Status == StockRequestStatuses.Approved
            ? $"{who} approved {request.ApprovedQuantity} of {request.Quantity} × {item}{OrderAudit.AtBranch(branch, request.LocationId)} ({request.RequestId})."
            : $"{who} rejected the request for {request.Quantity} × {item}{OrderAudit.AtBranch(branch, request.LocationId)} ({request.RequestId}).",
        OccurredAt = request.DecidedAt ?? DateTime.UtcNow,
        OperationId = $"stock-request-decided:{request.RequestId}",
        Metadata = RequestMetadata(request, "stock_request_decided", previousStatus),
    };

    /// <summary>The branch withdrew a request before IMS decided. Cancelled once (the status gate refuses a second).</summary>
    public static AuditEvent StockRequestCancelled(StockRequest request, string item, string? branch, string previousStatus) => new()
    {
        Module = PosModules.StockManagement,
        Action = AuditActions.Updated,
        Target = RequestTarget(request),
        Describe = who => $"{who} cancelled the request for {request.Quantity} × {item}{OrderAudit.AtBranch(branch, request.LocationId)} ({request.RequestId}).",
        OccurredAt = request.CancelledAt ?? DateTime.UtcNow,
        OperationId = $"stock-request-cancelled:{request.RequestId}",
        Metadata = new Dictionary<string, object?>(RequestMetadata(request, "stock_request_cancelled", previousStatus))
        {
            ["cancel_reason"] = request.CancelReason,
        },
    };

    private static AuditTarget RequestTarget(StockRequest request) =>
        new("stock_request", request.RequestId, $"Stock request {request.RequestId}");

    private static Dictionary<string, object?> RequestMetadata(StockRequest request, string operation, string? previousStatus) => new()
    {
        ["operation"] = operation,
        ["location_id"] = request.LocationId,
        ["variation_id"] = request.VariationId,
        ["sku"] = request.Sku,
        ["quantity"] = request.Quantity,
        ["approved_quantity"] = request.ApprovedQuantity,
        ["previous_status"] = previousStatus,
        ["new_status"] = request.Status,
    };

    public static string ItemLabel(ProductVariation? variation)
    {
        var product = variation?.Product?.ProductName;
        var name = variation?.VariationName;
        if (string.IsNullOrWhiteSpace(name)) return product ?? "item";
        var label = name.Contains('|')
            ? string.Join(" · ", name.Split('|').Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0))
            : name;
        return string.IsNullOrWhiteSpace(product) ? label : $"{product} ({label})";
    }

    private static AuditTarget AdjustmentTarget(StockAdjustment adjustment) =>
        new("stock_adjustment", Id(adjustment.AdjustmentId), $"Stock adjustment #{adjustment.AdjustmentId}");

    private static Dictionary<string, object?> AdjustmentMetadata(StockAdjustment adjustment, string operation, string? previousStatus) => new()
    {
        ["operation"] = operation,
        ["location_id"] = adjustment.LocationId,
        ["variation_id"] = adjustment.VariationId,
        ["quantity"] = adjustment.Quantity,
        ["adjustment_type"] = adjustment.AdjustmentType,
        ["previous_status"] = previousStatus,
        ["new_status"] = adjustment.Status,
    };

    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);
}
