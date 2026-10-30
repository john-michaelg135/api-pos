using System;

namespace Domains.Entities;

// Photo attached to a refund request (required for GCash-paid institutional orders).
// One per order; a new request after a rejection replaces it.
public class OrderRefundProof
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string ProofImage { get; set; } = string.Empty; // Browser-shrunk image as a data URL
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Order Order { get; set; } = null!;
}
