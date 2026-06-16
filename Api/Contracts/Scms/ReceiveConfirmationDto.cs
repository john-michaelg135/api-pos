namespace Api.Contracts.Scms;

public class ReceiveConfirmationDto
{
    public string BranchId { get; set; } = string.Empty;
    public string ReceivedBy { get; set; } = string.Empty;
    public List<ReceivedProductDto> ReceivedProducts { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}

public class ReceivedProductDto
{
    public string ProductId { get; set; } = string.Empty;
    public int ExpectedQuantity { get; set; }
    public int ReceivedQuantity { get; set; }
    public int DamagedQuantity { get; set; }
}
