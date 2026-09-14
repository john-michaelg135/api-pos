namespace Domains.Entities;

public class ProductVariation
{
    public int VariationId { get; set; }
    public int ProductId { get; set; }
    public string VariationName { get; set; } = string.Empty;

    // Reconciliation: canonical SKU shared with SCM. Historically the SKU was the first
    // pipe-segment of VariationName (derived client-side). This persists it as a first-class,
    // matchable identifier so transfer lines resolve to a variation without string parsing.
    public string? Sku { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? SyncedAt { get; set; }

    // Navigation
    public Product Product { get; set; } = null!;
    public ICollection<ProductPrice> ProductPrices { get; set; } = new List<ProductPrice>();
    public ICollection<PriceHistory> PriceHistories { get; set; } = new List<PriceHistory>();
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
