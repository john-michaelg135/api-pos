namespace Api.Contracts.OrderManagement;

public class OrderFilterDto
{
    public string? OrderStatus { get; set; }      // Pending, Processing, Completed, Cancelled
    public string? OrderSource { get; set; }      // POS, Ecommerce
    public string? OrderType { get; set; }        // Store, Bazaar, Online, Institutional
    public int? LocationId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    // With DateFrom: also return older orders that still need action (pending, processing,
    // paid, shipped, refund/void requested), so a date-limited list never hides open work.
    public bool IncludeOpen { get; set; }
}
