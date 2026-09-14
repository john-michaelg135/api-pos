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

    // ── Reconciliation tracking (POS side) ──
    // When the branch confirmed receipt (null = not yet received).
    public DateTime? ReceivedAt { get; set; }
    // Whether POS successfully notified SCM of the receipt (the status callback).
    // Pending = not attempted, Acknowledged = SCM confirmed, Failed = callback errored.
    public string StatusSyncState { get; set; } = "Pending"; // Pending, Acknowledged, Failed
    public DateTime? StatusSyncedAt { get; set; }
    // Reconciliation verdict computed by comparing expected vs received quantities.
    // Unreconciled = not yet checked, Matched = all lines balanced, Discrepancy = drift found.
    public string ReconciliationState { get; set; } = "Unreconciled"; // Unreconciled, Matched, Discrepancy
    public DateTime? ReconciledAt { get; set; }

    public ICollection<StockTransferItem> Items { get; set; } = new List<StockTransferItem>();
}
