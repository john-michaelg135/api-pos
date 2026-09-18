using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class CreateVariationDto
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(100)]
    [RegularExpression(@"^[a-zA-Z0-9\-_\| \.\,\/\(\)]+$", ErrorMessage = "Invalid SKU format.")]
    public string VariationName { get; set; } = string.Empty;

    [Range(0.01, 1000000.0)]
    public decimal InitialPrice { get; set; }
}
