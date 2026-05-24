using Api.Contracts.Shared;

namespace Api.Contracts.OrderEntry;

public class OrderResponseDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string OrderSource { get; set; } = string.Empty;
    public string? LocationName { get; set; }
    public decimal TotalAmount { get; set; }
    
    // Sprint 2 Discount Auditing Fields
    public string? AppliedVoucherCode { get; set; }
    public decimal? VoucherDiscountAmount { get; set; }

    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
    public List<PaymentResponseDto> Payments { get; set; } = new();
}
