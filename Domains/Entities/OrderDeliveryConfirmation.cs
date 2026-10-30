using System;

namespace Domains.Entities;

// Proof that an institutional order reached the customer (one per order).
public class OrderDeliveryConfirmation
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string ReceivedBy { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; }
    public string? CourierReference { get; set; } // Lalamove booking ID or tracking link
    public string? ProofImage { get; set; }       // Browser-shrunk image as a data URL
    public string? Notes { get; set; }
    public int? ConfirmedBy { get; set; }         // FK/Ref to user in auth_db (optional, stored as plain int)
    public string? ConfirmedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Order Order { get; set; } = null!;
}
