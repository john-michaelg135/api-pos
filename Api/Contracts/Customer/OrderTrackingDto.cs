namespace Api.Contracts.Customer;

public class OrderTrackingDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string CurrentStatus { get; set; } = string.Empty;
    
    // 1: Pending/Awaiting Stock, 2: Processing, 3: Shipped, 4: Delivered/Completed
    public int TrackingStage { get; set; } 
    public DateTime LastUpdatedAt { get; set; }
}
