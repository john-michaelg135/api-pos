namespace Api.Contracts.Scms;

/// <summary>
/// Sent to SCMS when a branch receives a delivery
/// (SCMS-POS-INTEGRATION-SPEC.md, ReceiveConfirmationDto).
/// </summary>
public class ReceiveConfirmationDto
{
    public string TransferId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public string ReceivedBy { get; set; } = string.Empty;
    public string ReceivedByName { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public List<ReceivedProductDto> ReceivedProducts { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
}

public class ReceivedProductDto
{
    public string ProductId { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public int ExpectedQuantity { get; set; }
    public int ReceivedQuantity { get; set; }
    public int DamagedQuantity { get; set; }
    public string? DiscrepancyReason { get; set; }
}
