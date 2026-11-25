namespace Applications.StaffAlerts;

/// <summary>
/// Title, body and POS destination for an alert. Text names the order, delivery, stock request or
/// item and the branch (plus stock quantities and IMS's remarks on a stock request decision), never
/// money amounts, customers, proof images or PINs: it is copied to the Portal inbox.
/// </summary>
public static class StaffAlertText
{
    public sealed record Content(string Title, string Body, string LinkPath);

    public static Content For(StaffAlertIntent intent, string? branchName, string? itemLabel)
    {
        var order = string.IsNullOrWhiteSpace(intent.OrderNumber) ? $"#{intent.OrderId}" : intent.OrderNumber;
        var at = string.IsNullOrWhiteSpace(branchName) ? string.Empty : $" at {branchName}";
        var orderLink = $"/order-management?orderId={intent.OrderId}";
        var partial = intent.Partial ? "partial " : string.Empty;

        return intent.Kind switch
        {
            StaffAlertKinds.RefundRequested => new(
                intent.Partial ? "Partial refund request needs review" : "Refund request needs review",
                $"Order {order}{at} has a {partial}refund request waiting for a manager.",
                orderLink),
            StaffAlertKinds.VoidRequested => new(
                "Void request needs review",
                $"Order {order}{at} has a void request waiting for a manager.",
                orderLink),
            StaffAlertKinds.RefundApproved => new(
                intent.Partial ? "Partial refund approved" : "Refund approved",
                $"Your {partial}refund request for order {order} was approved.",
                orderLink),
            StaffAlertKinds.RefundRejected => new(
                intent.Partial ? "Partial refund request rejected" : "Refund request rejected",
                $"Your {partial}refund request for order {order} was rejected.",
                orderLink),
            StaffAlertKinds.VoidApproved => new(
                "Void approved",
                $"Your void request for order {order} was approved.",
                orderLink),
            StaffAlertKinds.VoidRejected => new(
                "Void request rejected",
                $"Your void request for order {order} was rejected.",
                orderLink),
            StaffAlertKinds.TransferArrived => new(
                "Delivery ready to receive",
                string.IsNullOrWhiteSpace(intent.TransferSourceName)
                    ? $"Delivery {intent.TransferId} arrived{at}. Count it in Receive Stock."
                    : $"Delivery {intent.TransferId} from {intent.TransferSourceName} arrived{at}. Count it in Receive Stock.",
                $"/stock-management?tab=receive&transferId={Uri.EscapeDataString(intent.TransferId ?? string.Empty)}"),
            StaffAlertKinds.LowStock => new(
                "Low stock",
                $"{itemLabel ?? "An item"}{at} is below its minimum stock level.",
                $"/stock-management?tab=levels&locationId={intent.LocationId}&variationId={intent.VariationId}"),
            StaffAlertKinds.StockRequestApproved => new(
                "Stock request approved",
                Fit(ApprovedBody(intent, itemLabel ?? "an item", at)),
                StockRequestLink(intent)),
            StaffAlertKinds.StockRequestRejected => new(
                "Stock request rejected",
                RejectedBody(intent, itemLabel ?? "an item", at),
                StockRequestLink(intent)),
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, "Unknown staff alert kind."),
        };
    }

    /// <summary>The StaffAlerts.Body column; IMS remarks are shortened to fit it.</summary>
    public const int MaxBodyLength = 500;

    private static string ApprovedBody(StaffAlertIntent intent, string item, string at)
    {
        var approved = intent.ApprovedQuantity ?? 0;
        var requested = intent.RequestedQuantity ?? approved;
        var units = requested == 1 ? "unit" : "units";
        var amount = approved >= requested
            ? (requested == 1 ? $"the 1 requested {units}" : $"all {requested} requested {units}")
            : $"{approved} of {requested} requested {units}";
        return $"IMS approved {amount} of {item} for request {intent.StockRequestId}{at}. " +
               "You'll get another alert when the delivery arrives.";
    }

    private static string RejectedBody(StaffAlertIntent intent, string item, string at)
    {
        var head = $"IMS rejected request {intent.StockRequestId} for {item}{at}.";
        var remarks = intent.Remarks?.Trim();
        return Fit(string.IsNullOrEmpty(remarks) ? head : $"{head} Reason: {remarks}");
    }

    private static string Fit(string body) =>
        body.Length <= MaxBodyLength ? body : body[..(MaxBodyLength - 1)].TrimEnd() + "…";

    private static string StockRequestLink(StaffAlertIntent intent) =>
        $"/stock-management?tab=requests&locationId={intent.LocationId}&requestId={Uri.EscapeDataString(intent.StockRequestId ?? string.Empty)}";

    /// <summary>"Ube Halaya (Jar · 300g)" from a product and a "SKU|Jar|300g" variation name.</summary>
    public static string ItemLabel(string? productName, string? variationName)
    {
        var variation = variationName;
        if (!string.IsNullOrWhiteSpace(variation) && variation.Contains('|'))
            variation = string.Join(" · ", variation.Split('|').Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0));
        if (string.IsNullOrWhiteSpace(productName)) return string.IsNullOrWhiteSpace(variation) ? "An item" : variation!;
        return string.IsNullOrWhiteSpace(variation) ? productName : $"{productName} ({variation})";
    }
}
