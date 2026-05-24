namespace Domains.Entities;

public class OrderItem
{
    public int ItemId { get; set; }
    public int OrderId { get; set; }
    public int VariationId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }   // Snapshot of price at checkout time (POS-004)
    public decimal Subtotal { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Order Order { get; set; } = null!;
    public ProductVariation ProductVariation { get; set; } = null!;
}
