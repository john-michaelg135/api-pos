namespace Api.Contracts.OrderManagement;

public class OrderFilterDto
{
    public string? OrderStatus { get; set; }      // Pending, Processing, Completed, Cancelled
    public string? OrderSource { get; set; }      // POS, Ecommerce
    public string? OrderType { get; set; }        // Store, Bazaar, Online, Institutional
    public int? LocationId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}
