namespace Api.Contracts.OrderEntry;

public class CreateEcommerceOrderDto
{
    // Customer & Delivery
    public int? CustomerId { get; set; }
    public string? CustomerAuthId { get; set; }  // Auth GUID from ms-authentication
    public string? ContactPerson { get; set; }

    // Normalized address fields (US-POS-008 / US-EC-008)
    public string? InstitutionalStreet { get; set; }
    public string? InstitutionalCity { get; set; }
    public string? InstitutionalProvince { get; set; }
    public string? InstitutionalZipCode { get; set; }
    public string? DeliveryAddress { get; set; }   // Legacy flat string (backwards compat)

    public string OrderType { get; set; } = "Online";  // Online or Institutional
    public string PaymentMethod { get; set; } = "COD"; // COD, GCash, BankTransfer

    public bool ApplyPwdDiscount { get; set; } = false;

    public bool IsPreorder { get; set; } = false;
    public string? CustomVariationNotes { get; set; }

    public List<CartItemDto> Items { get; set; } = new();
}
