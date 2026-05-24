namespace Api.Contracts.ProductCatalog;

public class CreateProductDto
{
    public string ProductName { get; set; } = string.Empty;
    public string ProductCategory { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
}
