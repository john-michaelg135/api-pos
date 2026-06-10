namespace Api.Contracts.Crms;

/// <summary>Full order details returned by GET /api-pos/orders/{orderId}.</summary>
public class CrmsOrderDetailDto
{
    public int OrderId { get; set; }
    public string? CustomerId { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;

    public List<CrmsOrderItemDto> Items { get; set; } = new();

    public CrmsOrderPricingDto Pricing { get; set; } = new();

    public string? DeliveryAddress { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public class CrmsOrderItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}
