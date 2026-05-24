namespace Api.Contracts.OrderEntry;

public class CreateOrderDto
{
    public string OrderType { get; set; } = "Store";       // Store or Bazaar
    public int? LocationId { get; set; }
    public int? SubmittedBy { get; set; }                   // User ID (from auth)
    public string PaymentMethod { get; set; } = "Cash";
    
    // Sprint 2 Pricing Controls
    public bool ApplyPwdDiscount { get; set; } = false;
    public string? VoucherCode { get; set; }

    public List<CartItemDto> Items { get; set; } = new();
}
