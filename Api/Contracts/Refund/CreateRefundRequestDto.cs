using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.Refund;

public class CreateRefundRequestDto
{
    public int OrderId { get; set; }
    public int VariationId { get; set; }
    public int LocationId { get; set; }

    [Range(1, 10000)]
    public int QuantityToReturn { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string Reason { get; set; } = string.Empty;

    public int RequestedBy { get; set; }
}
