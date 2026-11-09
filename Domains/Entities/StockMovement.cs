namespace Domains.Entities;

public class StockMovement
{
    public long StockMovementId { get; set; }
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int QuantityDelta { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? OrderId { get; set; }
    public int? PerformedBy { get; set; }
    public DateTime OccurredAt { get; set; }

    public ProductVariation? Variation { get; set; }
    public Location? Location { get; set; }
    public Order? Order { get; set; }
}
