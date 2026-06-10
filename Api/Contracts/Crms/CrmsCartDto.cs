namespace Api.Contracts.Crms;

/// <summary>Customer cart returned by GET /api-pos/customers/{customerId}/cart.</summary>
public class CrmsCartDto
{
    public string CustomerId { get; set; } = string.Empty;
    public List<CrmsCartItemDto> Items { get; set; } = new();
    public CrmsCartPricingDto Pricing { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
}

public class CrmsCartItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
}

public class CrmsCartPricingDto
{
    public decimal SubtotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
}
