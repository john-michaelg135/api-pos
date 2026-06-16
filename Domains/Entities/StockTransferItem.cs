namespace Domains.Entities;

public class StockTransferItem
{
    public int Id { get; set; } // PK
    public string TransferId { get; set; } = string.Empty; // FK
    
    public string ProductId { get; set; } = string.Empty; // Provided by SCMS
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    
    public StockTransfer? Transfer { get; set; }
}
