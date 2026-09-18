using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class UpdateVariationDto
{
    [StringLength(100)]
    [RegularExpression(@"^[a-zA-Z0-9\-_\| \.\,\/\(\)]+$", ErrorMessage = "Invalid SKU format.")]
    public string? VariationName { get; set; }
    public bool? IsActive { get; set; }
}
