namespace Api.Contracts.Shared;

public class PaymentResponseDto
{
    public int PaymentId { get; set; }
    public int OrderId { get; set; }
    public decimal AmountPaid { get; set; }
    public string PaymentChannel { get; set; } = string.Empty;
    public string? GatewayReferenceNumber { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
}
