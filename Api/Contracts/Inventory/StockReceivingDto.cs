namespace Api.Contracts.Inventory;

public class StockReceivingDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int QuantityReceived { get; set; }
    public string? Notes { get; set; }
    public int? ReceivedBy { get; set; }
    public int? TransferId { get; set; }
}
