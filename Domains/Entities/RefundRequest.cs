namespace Domains.Entities;

public class RefundRequest
{
    public int RefundRequestId { get; set; }

    // References the original order being refunded
    public int OrderId { get; set; }

    // The specific variation being returned (needed to restore the correct stock)
    public int VariationId { get; set; }

    // FK to Location — copied from the Order at submission time for stock restore
    public int LocationId { get; set; }

    // Quantity the customer wants to return
    public int QuantityToReturn { get; set; }

    // Reason for the refund
    public string Reason { get; set; } = string.Empty;

    // US-POS-016: Always "Pending" on creation — manager must review
    public string Status { get; set; } = "Pending";

    // FK to user in auth_db (cashier/customer who requested the refund)
    public int RequestedBy { get; set; }

    // US-POS-017: Logged when manager approves — permanently auditable
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public ProductVariation? Variation { get; set; }
}
