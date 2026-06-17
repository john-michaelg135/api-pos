using Api.Contracts.Shared;

namespace Api.Contracts.OrderEntry;

public class OrderResponseDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string OrderSource { get; set; } = string.Empty;
    public string? LocationName { get; set; }
    public int? LocationId { get; set; }
    public decimal TotalAmount { get; set; }

    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public string? PaymentUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
    public List<PaymentResponseDto> Payments { get; set; } = new();
}
