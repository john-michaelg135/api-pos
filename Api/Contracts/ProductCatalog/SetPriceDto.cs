namespace Api.Contracts.ProductCatalog;

public class SetPriceDto
{
    public decimal Price { get; set; }
    public int? SetBy { get; set; }  // User ID (from auth service)
}
