namespace Applications.StaffAlerts;

/// <summary>
/// Title, body and POS destination for an alert. Text names the order, delivery or item and the
/// branch, never amounts, customers, proof images or PINs: it is copied to the Portal inbox.
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
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, "Unknown staff alert kind."),
        };
    }

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
