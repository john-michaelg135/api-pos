using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.StockAdjustment;

public class CreateStockAdjustmentDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }

    /// <summary>Type of adjustment: Damage, Loss, or Correction.</summary>
    public string AdjustmentType { get; set; } = string.Empty;

    /// <summary>Quantity to adjust (positive for addition, negative for deduction) — applied only AFTER manager approval (US-POS-019).</summary>
    [Range(-10000, 10000)]
    public int Quantity { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>User ID of the inventory manager who filed this report.</summary>
    public int SubmittedBy { get; set; }
}
