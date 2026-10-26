namespace Api.Contracts.OrderEntry;

public class CreateInstitutionalOrderDto
{
    public int? LocationId { get; set; }
    public string? DeliveryAddress { get; set; }
    public string? ContactPerson { get; set; }
    public string? CustomVariationNotes { get; set; }
    public string PaymentMethod { get; set; } = "COD";
    // GCash only: customer pays a 50% downpayment now and the balance later.
    public bool IsPartialPayment { get; set; }
    public decimal? AmountTendered { get; set; }
    public decimal? ChangeAmount { get; set; }
    public int? SubmittedBy { get; set; }
    public List<CartItemDto> Items { get; set; } = new();
}
