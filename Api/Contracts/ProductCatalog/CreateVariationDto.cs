namespace Api.Contracts.ProductCatalog;

public class CreateVariationDto
{
    public string VariationName { get; set; } = string.Empty;
    public decimal InitialPrice { get; set; }
}
