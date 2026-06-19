using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.OrderEntry;

public class CreateOrderDto
{
    public string OrderType { get; set; } = "Store";       // Store or Bazaar
    public int? LocationId { get; set; }
    public int? SubmittedBy { get; set; }                   // User ID (from auth)
    public string PaymentMethod { get; set; } = "Cash";
    
    // Sprint 2 Pricing Controls
    public bool ApplyPwdDiscount { get; set; } = false;
    public List<CartItemDto> Items { get; set; } = new();

    // Customer / PWD Details
    public string? ContactPerson { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? InstitutionalStreet { get; set; }
    public string? InstitutionalCity { get; set; }
    public string? InstitutionalProvince { get; set; }
    public string? InstitutionalZipCode { get; set; }
    public string? CustomVariationNotes { get; set; }

    // Dedicated Senior/PWD Fields
    public string? SeniorPwdId { get; set; }
    public string? SeniorPwdName { get; set; }
    public string? SeniorPwdStreet { get; set; }
    public string? SeniorPwdBarangay { get; set; }
    public string? SeniorPwdCity { get; set; }
    public string? SeniorPwdProvince { get; set; }
    public string? SeniorPwdZipCode { get; set; }
}
