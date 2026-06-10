using System;

namespace Api.Contracts.Inventory;

public class StockReceivingResponseDto
{
    public int ReceivingId { get; set; }
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int QuantityReceived { get; set; }
    public string? Notes { get; set; }
    public int? ReceivedBy { get; set; }
    public DateTime ReceivedAt { get; set; }
}
