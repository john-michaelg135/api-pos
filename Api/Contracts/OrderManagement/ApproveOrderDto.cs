namespace Api.Contracts.OrderManagement;

public class ApproveOrderDto
{
    public int ApprovedBy { get; set; }
    public string? PinCode { get; set; }
}
