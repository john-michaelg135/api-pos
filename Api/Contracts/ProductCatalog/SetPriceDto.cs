using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class SetPriceDto
{
    [Range(0.01, 1000000.0)]
    public decimal Price { get; set; }
    public int? SetBy { get; set; }  // User ID (from auth service)
}
