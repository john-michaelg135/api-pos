namespace Domains.Entities;

public class StockTransfer
{
    public string TransferId { get; set; } = string.Empty; // PK, provided by SCMS (e.g. trans_001)
    
    public string SourceLocationId { get; set; } = string.Empty;
    public string SourceLocationName { get; set; } = string.Empty;
    
    public string DestinationBranchId { get; set; } = string.Empty;
    public string DestinationBranchName { get; set; } = string.Empty;
    
    public DateTime TransferDate { get; set; }
    public string Status { get; set; } = "In Transit"; // In Transit, Pending Receiving, Completed
    
    public ICollection<StockTransferItem> Items { get; set; } = new List<StockTransferItem>();
}
