namespace Api.Contracts.OrderManagement;

public class RejectOrderDto
{
    public int RejectedBy { get; set; }
    public string RejectionRemarks { get; set; } = string.Empty;
}
