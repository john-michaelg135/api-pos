namespace Domains.Entities;

public class StockTransferItem
{
    public int Id { get; set; } // PK
    public string TransferId { get; set; } = string.Empty; // FK
    
    public string ProductId { get; set; } = string.Empty; // Provided by SCMS
    public string? Sku { get; set; }                       // Canonical SKU from SCM (reconciliation key)
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }                       // Expected quantity (from the manifest)

    // ── Reconciliation tracking ──
    // POS variation this line resolved to (null = unmatched, blocks receipt).
    public int? ResolvedVariationId { get; set; }
    // Quantity actually received into POS stock for this line (null = not yet received).
    public int? ReceivedQuantity { get; set; }
    // Quantity reported damaged/short on receipt.
    public int DamagedQuantity { get; set; }

    public StockTransfer? Transfer { get; set; }
}
