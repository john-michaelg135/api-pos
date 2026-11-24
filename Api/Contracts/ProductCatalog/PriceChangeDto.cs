namespace Api.Contracts.ProductCatalog;

/// <summary>
/// One selling price a variation has had, with the price it replaced. Built from every
/// saved price row, so the timeline runs from the first price to the current one.
/// </summary>
public class PriceChangeDto
{
    public int PriceId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;

    public decimal Price { get; set; }
    /// <summary>The price this one replaced; null for the variation's first price.</summary>
    public decimal? PreviousPrice { get; set; }
    /// <summary>Price less the previous price; null for the first price.</summary>
    public decimal? Change { get; set; }
    /// <summary>Change as a percentage of the previous price; null for the first price.</summary>
    public decimal? ChangePercent { get; set; }

    public DateTime EffectiveFrom { get; set; }
    /// <summary>When the next price replaced it; null while it is the current price.</summary>
    public DateTime? EffectiveTo { get; set; }
    public bool IsCurrent { get; set; }

    public int? SetBy { get; set; }
    /// <summary>Who set it, when known (staff recorded by the POS).</summary>
    public string? SetByName { get; set; }
}
