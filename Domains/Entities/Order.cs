namespace Domains.Entities;

public class Order
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public int? CustomerId { get; set; }     // FK to customers (nullable for walk-in)
    public string? CustomerAuthId { get; set; } // Auth GUID from ms-authentication
    public int? LocationId { get; set; }     // FK to locations
    public string OrderType { get; set; } = "Store";       // Store, Bazaar, Online, Institutional
    public string OrderSource { get; set; } = "POS";       // POS or Ecommerce
    public string? DeliveryAddress { get; set; }
    
    // Normalized Institutional Address Fields (Sprint 2 Panel Requirement)
    public string? InstitutionalStreet { get; set; }
    public string? InstitutionalCity { get; set; }
    public string? InstitutionalProvince { get; set; }
    public string? InstitutionalZipCode { get; set; }

    public string? ContactPerson { get; set; }
    public bool IsPreorder { get; set; } = false;
    public string? CustomVariationNotes { get; set; }

    // Dedicated Senior/PWD Fields
    public string? SeniorPwdId { get; set; }
    public string? SeniorPwdName { get; set; }
    public string? SeniorPwdStreet { get; set; }
    public string? SeniorPwdBarangay { get; set; }
    public string? SeniorPwdCity { get; set; }
    public string? SeniorPwdProvince { get; set; }
    public string? SeniorPwdZipCode { get; set; }
    
    // Legacy payment fields preserved for API backwards compatibility during gateway transitions
    public string PaymentMethod { get; set; } = "Cash";    // Cash, GCash, BankTransfer, COD
    public string PaymentStatus { get; set; } = "Pending"; // Pending, Paid, Refunded
    
    public string OrderStatus { get; set; } = "Pending";   // Pending, Processing, etc.
    public decimal TotalAmount { get; set; }

    public int? SubmittedBy { get; set; }    // FK to users (in auth_db, stored as plain int)
    public int? ApprovedBy { get; set; }     // FK to users (in auth_db, stored as plain int)
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionRemarks { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Location? Location { get; set; }
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<OrderStatusHistory> StatusHistory { get; set; } = new List<OrderStatusHistory>();
}