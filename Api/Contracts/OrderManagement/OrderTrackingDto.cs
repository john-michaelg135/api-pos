namespace Api.Contracts.OrderManagement;

/// <summary>
/// Lightweight order tracking response for EC-012 progress bar.
/// </summary>
public class OrderTrackingDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderSource { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public bool IsPreorder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }

    // Derived progress step: Pending → Processing → Shipped → Completed | Cancelled
    public string ProgressStep => OrderStatus switch
    {
        "Pending"    => "Pending",
        "Processing" => "Processing",
        "Shipped"    => "Shipped",
        "Completed"  => "Completed",
        "Cancelled"  => "Cancelled",
        _            => OrderStatus
    };
}
