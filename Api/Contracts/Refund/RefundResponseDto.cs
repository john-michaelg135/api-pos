namespace Api.Contracts.Refund;

public class RefundResponseDto
{
    public int RefundRequestId { get; set; }
    public int OrderId { get; set; }
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int QuantityToReturn { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int RequestedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
