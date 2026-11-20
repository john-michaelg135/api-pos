namespace Api.Contracts.OrderManagement;

public class ConfirmInstitutionalDeliveryDto
{
    public string ReceivedBy { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; }
    public string? CourierReference { get; set; }
    public string? ProofImage { get; set; } // Legacy: data:image/...;base64,... shrunk in the browser
    public string? ProofReference { get; set; } // Ready reference from proof-uploads/delivery/confirm (send one or the other)
    public string? Notes { get; set; }
    public int ConfirmedBy { get; set; }
}
