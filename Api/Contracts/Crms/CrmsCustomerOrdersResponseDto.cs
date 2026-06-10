namespace Api.Contracts.Crms;

/// <summary>Envelope returned by GET /api-pos/customers/{customerId}/orders.</summary>
public class CrmsCustomerOrdersResponseDto
{
    public List<CrmsOrderSummaryDto> Items { get; set; } = new();
}
