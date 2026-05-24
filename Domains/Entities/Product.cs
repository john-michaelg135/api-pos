namespace Domains.Entities;

public class Product
{
    public int ProductId { get; set; }
    public string? ScmsProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string ProductCategory { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? SyncedAt { get; set; }

    // Navigation
    public ICollection<ProductVariation> Variations { get; set; } = new List<ProductVariation>();
}
