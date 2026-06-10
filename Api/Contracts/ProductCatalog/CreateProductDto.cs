using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class CreateProductDto
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(30)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string ProductName { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string ProductCategory { get; set; } = string.Empty;

    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
}
