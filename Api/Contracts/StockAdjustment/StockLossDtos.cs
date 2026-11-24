using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.StockAdjustment;

/// <summary>Why units became unsellable. Recorded on the loss so it can be reported by cause.</summary>
public static class StockLossReasons
{
    public const string Expired = "Expired";
    public const string Damaged = "Damaged";
    public const string Mishandled = "Mishandled";
    public const string Other = "Other";

    public static readonly string[] All = [Expired, Damaged, Mishandled, Other];
}

/// <summary>A manager writes off units that can no longer be sold. Stock goes down straight away.</summary>
public class RecordStockLossDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }

    /// <summary>Units lost; always positive.</summary>
    [Range(1, 10000)]
    public int Quantity { get; set; }

    /// <summary>One of <see cref="StockLossReasons.All"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string LossReason { get; set; } = string.Empty;

    [StringLength(500)]
    [RegularExpression(@"^[^<>]*$", ErrorMessage = "HTML tags are not allowed.")]
    public string? Notes { get; set; }
}

public class StockLossResponseDto
{
    public int LossId { get; set; }
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    /// <summary>Units lost (positive).</summary>
    public int Quantity { get; set; }
    public string LossReason { get; set; } = string.Empty;
    public string? Notes { get; set; }
    /// <summary>Selling price per unit when the loss was recorded.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Quantity × unit price: the sales value written off.</summary>
    public decimal? LossValue { get; set; }
    public string? RecordedByName { get; set; }
    public DateTime RecordedAt { get; set; }
}
