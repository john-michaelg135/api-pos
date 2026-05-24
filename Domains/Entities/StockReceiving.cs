namespace Domains.Entities;

public class StockReceiving
{
    public int ReceivingId { get; set; }
    public int VariationId { get; set; }        // FK to ProductVariation
    public int LocationId { get; set; }         // FK to Location
    public int QuantityReceived { get; set; }
    public string? Notes { get; set; }
    public int? ReceivedBy { get; set; }        // FK to user (auth_db)
    public DateTime ReceivedAt { get; set; }

    // Navigation
    public ProductVariation? Variation { get; set; }
    public Location? Location { get; set; }
}
