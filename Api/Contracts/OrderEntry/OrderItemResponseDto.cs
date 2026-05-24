namespace Api.Contracts.OrderEntry;

public class OrderItemResponseDto
{
    public int ItemId { get; set; }
    public int VariationId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string VariationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
}
