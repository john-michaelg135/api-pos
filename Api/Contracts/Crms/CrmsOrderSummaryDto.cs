namespace Api.Contracts.Crms;

/// <summary>
/// One row per OrderItem in the customer orders list.
/// This gives CRMS the most granular view — an order with 3 items produces 3 rows.
/// </summary>
public class CrmsOrderSummaryDto
{
    public int OrderId { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;

    public CrmsProductRefDto Product { get; set; } = new();

    public int Quantity { get; set; }

    public CrmsOrderPricingDto Pricing { get; set; } = new();

    public string? DeliveryAddress { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public class CrmsProductRefDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
}

public class CrmsOrderPricingDto
{
    /// <summary>Sum of all item subtotals for this order (before voucher).</summary>
    public decimal SubtotalAmount { get; set; }
    public string? VoucherCode { get; set; }
    public decimal? VoucherDiscountAmount { get; set; }
    /// <summary>Final amount = SubtotalAmount - VoucherDiscountAmount.</summary>
    public decimal TotalAmount { get; set; }
}
