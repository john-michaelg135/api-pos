namespace Api.Contracts.ProductCatalog;

public class PriceHistoryResponseDto
{
    public int HistoryId { get; set; }
    public decimal Price { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public int? SetBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
