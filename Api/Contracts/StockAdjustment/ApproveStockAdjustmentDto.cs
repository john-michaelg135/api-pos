namespace Api.Contracts.StockAdjustment;

public class ApproveStockAdjustmentDto
{
    /// <summary>User ID of the manager approving the stock deduction (from auth_db).</summary>
    public int ApprovedBy { get; set; }
}
