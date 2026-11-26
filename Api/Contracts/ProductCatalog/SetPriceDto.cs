using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class SetPriceDto
{
    [MaxLength(350000)]
    [RegularExpression(@"data:image/jpeg;base64,/9j/[A-Za-z0-9+/]*={0,2}")]
    public string? ProductImage { get; set; }

    [Range(0.01, 1000000.0)]
    public decimal Price { get; set; }
    public int? SetBy { get; set; }  // User ID (from auth service)
}
