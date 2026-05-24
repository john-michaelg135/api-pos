namespace Api.Contracts.Inventory;

public class StockResponseDto
{
    public int StockId { get; set; }
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime UpdatedAt { get; set; }
}
