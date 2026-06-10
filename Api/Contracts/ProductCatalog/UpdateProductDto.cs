using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.ProductCatalog;

public class UpdateProductDto
{
    [StringLength(30)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string? ProductName { get; set; }

    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string? ProductCategory { get; set; }

    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
    public bool? IsActive { get; set; }
}
