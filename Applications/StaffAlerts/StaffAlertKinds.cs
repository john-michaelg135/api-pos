using Domains.Entities;

namespace Applications.StaffAlerts;

public static class StaffAlertKinds
{
    public const string RefundRequested = "pos.refund-requested";
    public const string VoidRequested = "pos.void-requested";
    public const string RefundApproved = "pos.refund-approved";
    public const string RefundRejected = "pos.refund-rejected";
    public const string VoidApproved = "pos.void-approved";
    public const string VoidRejected = "pos.void-rejected";
    public const string TransferArrived = "pos.transfer-arrived";
    public const string LowStock = "pos.low-stock";
    public const string StockRequestApproved = "pos.stock-request-approved";
    public const string StockRequestRejected = "pos.stock-request-rejected";

    /// <summary>Content-free refresh notice on the shared POS staff channel.</summary>
    public const string StaffChanged = "pos.staff.changed";

    public static bool IsRequest(string kind) => kind is RefundRequested or VoidRequested;
    public static bool IsOrderDecision(string kind) => kind is RefundApproved or RefundRejected or VoidApproved or VoidRejected;
    public static bool IsStockRequestDecision(string kind) => kind is StockRequestApproved or StockRequestRejected;

    /// <summary>A decision on someone's own request: it goes to that requester alone.</summary>
    public static bool IsDecision(string kind) => IsOrderDecision(kind) || IsStockRequestDecision(kind);
    public static bool IsOrder(string kind) => IsRequest(kind) || IsOrderDecision(kind);

    public static string ModuleFor(string kind) => IsOrder(kind)
        ? StaffAlertAccess.OrderManagementModule
        : StaffAlertAccess.StockManagementModule;

    public static string AudienceFor(string kind) => IsDecision(kind)
        ? StaffAlertAudiences.Requester
        : StaffAlertAudiences.Branch;
}

/// <summary>
/// What a service asks the DbContext to raise with its next save. Order alerts point at the status
/// history row being saved (its ID is the occurrence); the rest carry their own occurrence key.
/// </summary>
public sealed class StaffAlertIntent
{
    public required string Kind { get; init; }
    public OrderStatusHistory? History { get; init; }
    public string? OccurrenceKey { get; init; }
    public int? LocationId { get; init; }
    public int? OrderId { get; init; }
    public string? OrderNumber { get; init; }
    public string? TransferId { get; init; }
    public string? TransferSourceName { get; init; }
    public int? VariationId { get; init; }
    /// <summary>Stock request decisions: the request ("SR-000123") and what IMS decided on it.</summary>
    public string? StockRequestId { get; init; }
    public int? RequestedQuantity { get; init; }
    public int? ApprovedQuantity { get; init; }
    public string? Remarks { get; init; }
    /// <summary>Request alerts: the verified requester. Decision alerts: the only recipient.</summary>
    public string? PersonPortalUserId { get; init; }
    public bool Partial { get; init; }

    public string ResolveKey() => History is not null
        ? $"order-history:{History.Id}"
        : OccurrenceKey ?? throw new InvalidOperationException("A staff alert needs an occurrence.");

    public static string LowStockKey(int locationId, int variationId, int episode) =>
        $"low-stock:{locationId}:{variationId}:{episode}";

    public static string TransferKey(string transferId) => $"transfer-arrived:{transferId}";

    /// <summary>IMS decides a stock request once (approve or reject), so the request is the occurrence.</summary>
    public static string StockRequestDecisionKey(string requestId) => $"stock-request-decided:{requestId}";
}
