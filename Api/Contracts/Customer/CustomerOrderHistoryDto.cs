namespace Api.Contracts.Customer;

public class CustomerOrderHistoryDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public List<CustomerOrderHistoryItemDto> Items { get; set; } = new();
}

public class CustomerOrderHistoryItemDto
{
    public string ProductName { get; set; } = string.Empty;
    public string VariationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}
