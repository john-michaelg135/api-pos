namespace Domains.Entities;

public class Payment
{
    public int PaymentId { get; set; }
    public int OrderId { get; set; }
    public decimal AmountPaid { get; set; }
    public string PaymentChannel { get; set; } = "Cash"; // Cash, GCash, Maya, COD
    public string? GatewayReferenceNumber { get; set; }
    public string PaymentStatus { get; set; } = "Pending"; // Pending, Success, Failed
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Order? Order { get; set; }
}
