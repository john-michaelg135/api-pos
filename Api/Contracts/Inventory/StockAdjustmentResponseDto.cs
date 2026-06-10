namespace Api.Contracts.Inventory;

public class StockAdjustmentResponseDto
{
    public int AdjustmentId { get; set; }
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int Quantity { get; set; }
    public string AdjustmentType { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int SubmittedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
