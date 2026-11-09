namespace Api.Contracts.Inventory;

public class StockMovementResponseDto
{
    public long StockMovementId { get; set; }
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int QuantityDelta { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? OrderId { get; set; }
    public DateTime OccurredAt { get; set; }
    public int QuantityBefore { get; set; }
    public int QuantityAfter { get; set; }
}
