namespace Domains.Entities;

public class PriceHistory
{
    public int HistoryId { get; set; }
    public int VariationId { get; set; }
    public decimal Price { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public int? SetBy { get; set; }  // FK to users (in auth_db, stored as plain int)
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ProductVariation ProductVariation { get; set; } = null!;
}
