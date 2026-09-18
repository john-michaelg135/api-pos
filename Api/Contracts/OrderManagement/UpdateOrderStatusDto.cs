namespace Api.Contracts.OrderManagement;

public class UpdateOrderStatusDto
{
    public string Status { get; set; } = string.Empty;
    public int UpdatedBy { get; set; }
}
