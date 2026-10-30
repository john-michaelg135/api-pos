namespace Api.Contracts.OrderEntry;

public class ProductGridItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductImage { get; set; }
    public string ProductCategory { get; set; } = string.Empty;
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    /// <summary>False when no selling price is set yet; such items can't be sold.</summary>
    public bool HasPrice { get; set; } = true;

    // EC-004: Stock availability (null if no locationId filter applied)
    public int? StockQuantity { get; set; }
    public bool IsInStock { get; set; } = true;
}
