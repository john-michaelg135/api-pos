namespace Api.Contracts.Refund;

public class ApproveRefundDto
{
    /// <summary>User ID of the manager approving the refund (from auth_db).</summary>
    public int ApprovedBy { get; set; }
}
