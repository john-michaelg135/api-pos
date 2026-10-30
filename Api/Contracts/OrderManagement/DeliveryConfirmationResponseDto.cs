namespace Api.Contracts.OrderManagement;

// The proof photo is left out to keep order lists light; fetch it from
// GET orders/{orderId}/delivery-proof when HasProofImage is true.
public class DeliveryConfirmationResponseDto
{
    public string ReceivedBy { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; }
    public string? CourierReference { get; set; }
    public string? Notes { get; set; }
    public bool HasProofImage { get; set; }
    public int? ConfirmedBy { get; set; }
    public string? ConfirmedByName { get; set; }
    public DateTime ConfirmedAt { get; set; }
}
