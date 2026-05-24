namespace Api.Contracts.OrderEntry;

public class CreateInstitutionalOrderDto
{
    public int? LocationId { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? ContactPerson { get; set; }
    public string? CustomVariationNotes { get; set; }
    public string PaymentMethod { get; set; } = "COD";
    public int? SubmittedBy { get; set; }
    public List<CartItemDto> Items { get; set; } = new();
}
