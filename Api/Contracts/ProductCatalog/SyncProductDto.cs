namespace Api.Contracts.ProductCatalog;

/// <summary>
/// A product and its variations as published by SCMS (SCMS-POS-INTEGRATION-SPEC.md, Module 1).
/// SCMS owns these details; POS owns the selling price (SCMS may suggest a starting one).
/// </summary>
public class SyncProductDto
{
    public string ScmsProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductCategory { get; set; } = string.Empty;
    public string? ProductDescription { get; set; }
    public string? ProductImage { get; set; }
    public bool IsActive { get; set; } = true;
    public List<SyncVariationDto> Variations { get; set; } = new();
}

public class SyncVariationDto
{
    public string Sku { get; set; } = string.Empty;
    /// <summary>"SKU|Packaging|Size"; built from the parts when SCMS leaves it out.</summary>
    public string? VariationName { get; set; }
    public string PackagingType { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Optional selling price (₱). Used only while the variation has no POS price yet;
    /// once a POS manager prices it, later syncs never change it.
    /// </summary>
    public decimal? Price { get; set; }
}

public class SyncProductResultDto
{
    public int ProductId { get; set; }
    public string ScmsProductId { get; set; } = string.Empty;
    public int SyncedVariationsCount { get; set; }
    /// <summary>Variations that took SCMS's price as their starting price on this sync.</summary>
    public int StartingPricesSetCount { get; set; }
    /// <summary>Variations with no selling price yet; they stay off the sales screen until a manager prices them.</summary>
    public int PendingPricingCount { get; set; }
}
