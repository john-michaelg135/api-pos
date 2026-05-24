namespace Api.Contracts.Inventory;

public class LowStockAlertDto
{
    public int StockId { get; set; }
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int MinThreshold { get; set; }
    public int Deficit => MinThreshold - Quantity; // How many units below threshold
}
