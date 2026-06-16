namespace Api.Contracts.Scms;

public class BranchInventoryDto
{
    public string BranchId { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public List<BranchInventoryItemDto> Items { get; set; } = new();
}

public class BranchInventoryItemDto
{
    public string ProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int AvailableStocks { get; set; }
    public int MinimumStockLevel { get; set; }
    public string StockStatus { get; set; } = string.Empty;
}
