namespace Api.Contracts.ProductCatalog;

public class VariationResponseDto
{
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal? CurrentPrice { get; set; }
}
