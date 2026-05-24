namespace Api.Contracts.ProductCatalog;

public class UpdateProductDto
{
    public string? ProductName { get; set; }
    public string? ProductCategory { get; set; }
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
    public bool? IsActive { get; set; }
}
