using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.Inventory;

public class StockAdjustmentRequestDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }

    [Range(-10000, 10000)]
    public int Quantity { get; set; } // Can be negative for damage, positive for correction

    public string AdjustmentType { get; set; } = string.Empty; // Damage, Correction, Audit

    [Required(AllowEmptyStrings = false)]
    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string Reason { get; set; } = string.Empty;

    public int SubmittedBy { get; set; }
}
