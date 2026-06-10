namespace Domains.Entities;

public class StockAdjustment
{
    public int AdjustmentId { get; set; }

    // FK to ProductVariation (which item is damaged/lost)
    public int VariationId { get; set; }

    // FK to Location (which warehouse/store the stock is at)
    public int LocationId { get; set; }

    // Type of adjustment: Damage, Loss, Correction
    public string AdjustmentType { get; set; } = string.Empty;

    // Quantity to deduct — NOT applied until manager approves (US-POS-019)
    public int Quantity { get; set; }

    // Reason/description of the damage or loss
    public string Reason { get; set; } = string.Empty;

    // US-POS-019: Always "PendingApproval" on creation — stock unchanged at this point
    public string Status { get; set; } = "PendingApproval";

    // FK to user in auth_db (inventory manager who filed the report)
    public int SubmittedBy { get; set; }

    // US-POS-020: Logged when manager approves — permanently auditable
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation
    public ProductVariation? Variation { get; set; }
    public Location? Location { get; set; }
}
