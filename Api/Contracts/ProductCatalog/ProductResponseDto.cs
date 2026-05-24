namespace Api.Contracts.ProductCatalog;

public class ProductResponseDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductCategory { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
    public bool IsActive { get; set; }
    public List<VariationResponseDto> Variations { get; set; } = new();
}
