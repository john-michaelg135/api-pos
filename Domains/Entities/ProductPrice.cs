namespace Domains.Entities;

public class ProductPrice
{
    public int PriceId { get; set; }
    public int VariationId { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
    public int? SetBy { get; set; }  // FK to users (in auth_db, stored as plain int)
    public DateTime EffectiveFrom { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ProductVariation ProductVariation { get; set; } = null!;
}
