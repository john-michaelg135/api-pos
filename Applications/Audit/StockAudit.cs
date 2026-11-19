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
