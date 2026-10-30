using Applications.Interfaces;
using Api.Contracts.ProductCatalog;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class ProductCatalogService : IProductCatalogService
{
    private readonly PosDbContext _db;
    private readonly IAuditLogService _auditLogService;

    public ProductCatalogService(PosDbContext db, IAuditLogService auditLogService)
    {
        _db = db;
        _auditLogService = auditLogService;
    }

    // ────────────────────────────────────────────────────
    // POS-001: Product CRUD
    // ────────────────────────────────────────────────────

    public async Task<ProductResponseDto> CreateProductAsync(CreateProductDto dto)
    {
        var product = new Product
        {
            ProductName = dto.ProductName,
            ProductCategory = dto.ProductCategory,
            ProductDescription = dto.ProductDescription,
            ProductImage = dto.ProductImage,
            IsActive = true
        };

        await _db.Products.AddAsync(product);
        await _db.SaveChangesAsync();

        var response = MapToProductResponse(product);

        _auditLogService.Log(
            action: "Create",
            entity: "Product",
            entityId: product.ProductId,
            before: null,
            after: response,
            performedBy: null
        );

        return response;
    }

    public async Task<ProductResponseDto?> UpdateProductAsync(int productId, UpdateProductDto dto)
    {
        var product = await _db.Products
            .Include(p => p.Variations)
                .ThenInclude(v => v.ProductPrices.Where(pp => pp.IsActive))
            .FirstOrDefaultAsync(p => p.ProductId == productId);

        if (product == null) return null;

        if (dto.ProductName != null) product.ProductName = dto.ProductName;
        if (dto.ProductCategory != null) product.ProductCategory = dto.ProductCategory;
        if (dto.ProductDescription != null) product.ProductDescription = dto.ProductDescription;
        if (dto.ProductImage != null) product.ProductImage = dto.ProductImage;
        if (dto.IsActive.HasValue) product.IsActive = dto.IsActive.Value;

        _db.Products.Update(product);
        await _db.SaveChangesAsync();

        var response = MapToProductResponse(product);

        _auditLogService.Log(
            action: "Update",
            entity: "Product",
            entityId: product.ProductId,
            before: null, // Keep simple for now
            after: response,
            performedBy: null
        );

        return response;
    }

    public async Task<List<ProductResponseDto>> GetAllProductsAsync()
    {
        var products = await _db.Products
            .Include(p => p.Variations)
                .ThenInclude(v => v.ProductPrices.Where(pp => pp.IsActive))
            .OrderBy(p => p.ProductName)
            .ToListAsync();

        return products.Select(MapToProductResponse).ToList();
    }

    public async Task<ProductResponseDto?> GetProductByIdAsync(int productId)
    {
        var product = await _db.Products
            .Include(p => p.Variations)
                .ThenInclude(v => v.ProductPrices.Where(pp => pp.IsActive))
            .FirstOrDefaultAsync(p => p.ProductId == productId);

        return product == null ? null : MapToProductResponse(product);
    }

    // ────────────────────────────────────────────────────
    // POS-002: Variation management
    // ────────────────────────────────────────────────────

    public async Task<VariationResponseDto?> CreateVariationAsync(int productId, CreateVariationDto dto)
    {
        // Validate parent product exists
        var product = await _db.Products.FindAsync(productId);
        if (product == null) return null;

        var variation = new ProductVariation
        {
            ProductId = productId,
            VariationName = dto.VariationName,
            IsActive = true
        };

        await _db.ProductVariations.AddAsync(variation);
        await _db.SaveChangesAsync();

        // Create initial price record
        var now = DateTime.UtcNow;
        var price = new ProductPrice
        {
            VariationId = variation.VariationId,
            Price = dto.InitialPrice,
            IsActive = true,
            EffectiveFrom = now,
            CreatedAt = now
        };

        await _db.ProductPrices.AddAsync(price);
        await _db.SaveChangesAsync();

        return new VariationResponseDto
        {
            VariationId = variation.VariationId,
            VariationName = variation.VariationName,
            IsActive = variation.IsActive,
            CurrentPrice = dto.InitialPrice
        };
    }

    public async Task<VariationResponseDto?> UpdateVariationAsync(int variationId, UpdateVariationDto dto)
    {
        var variation = await _db.ProductVariations
            .Include(v => v.ProductPrices.Where(pp => pp.IsActive))
            .FirstOrDefaultAsync(v => v.VariationId == variationId);

        if (variation == null) return null;

        if (dto.VariationName != null) variation.VariationName = dto.VariationName;
        if (dto.IsActive.HasValue) variation.IsActive = dto.IsActive.Value;

        _db.ProductVariations.Update(variation);
        await _db.SaveChangesAsync();

        var activePrice = variation.ProductPrices.FirstOrDefault(pp => pp.IsActive);

        return new VariationResponseDto
        {
            VariationId = variation.VariationId,
            VariationName = variation.VariationName,
            IsActive = variation.IsActive,
            CurrentPrice = activePrice?.Price
        };
    }

    // ────────────────────────────────────────────────────
    // POS-003: Price management (auto-creates price history)
    // POS-004: Price snapshot logic
    // ────────────────────────────────────────────────────

    public async Task<VariationResponseDto?> SetVariationPriceAsync(int variationId, SetPriceDto dto)
    {
        var variation = await _db.ProductVariations
            .Include(v => v.ProductPrices)
            .FirstOrDefaultAsync(v => v.VariationId == variationId);

        if (variation == null) return null;

        var now = DateTime.UtcNow;

        // Find current active price and archive it
        var currentActivePrice = variation.ProductPrices.FirstOrDefault(pp => pp.IsActive);
        if (currentActivePrice != null)
        {
            // 1. Mark old price as inactive
            currentActivePrice.IsActive = false;

            // 2. Create price history record for the old price
            var history = new PriceHistory
            {
                VariationId = variationId,
                Price = currentActivePrice.Price,
                EffectiveFrom = currentActivePrice.EffectiveFrom,
                EffectiveTo = now,
                SetBy = currentActivePrice.SetBy,
                CreatedAt = now
            };

            await _db.PriceHistories.AddAsync(history);
        }

        // 3. Create new active price
        var newPrice = new ProductPrice
        {
            VariationId = variationId,
            Price = dto.Price,
            IsActive = true,
            SetBy = dto.SetBy,
            EffectiveFrom = now,
            CreatedAt = now
        };

        await _db.ProductPrices.AddAsync(newPrice);
        await _db.SaveChangesAsync();

        return new VariationResponseDto
        {
            VariationId = variation.VariationId,
            VariationName = variation.VariationName,
            IsActive = variation.IsActive,
            CurrentPrice = dto.Price
        };
    }

    // ────────────────────────────────────────────────────
    // POS-004: Price history
    // ────────────────────────────────────────────────────

    public async Task<List<PriceHistoryResponseDto>> GetPriceHistoryAsync(int variationId)
    {
        var history = await _db.PriceHistories
            .Where(h => h.VariationId == variationId)
            .OrderByDescending(h => h.EffectiveFrom)
            .Select(h => new PriceHistoryResponseDto
            {
                HistoryId = h.HistoryId,
                Price = h.Price,
                EffectiveFrom = h.EffectiveFrom,
                EffectiveTo = h.EffectiveTo,
                SetBy = h.SetBy,
                CreatedAt = h.CreatedAt
            })
            .ToListAsync();

        return history;
    }

    // ────────────────────────────────────────────────────
    // SCMS Module 1: product & variation sync
    // SCMS owns names, categories and SKUs; POS owns the selling price. A price SCMS sends is
    // only a starting price for a variation POS hasn't priced yet; without one, the variation
    // stays off the sales screen until a manager prices it.
    // ────────────────────────────────────────────────────

    public async Task<SyncProductResultDto> SyncProductFromScmsAsync(SyncProductDto dto)
    {
        ValidateSync(dto);
        var now = DateTime.UtcNow;
        var scmsId = dto.ScmsProductId.Trim();

        // Match by SCMS ID; on the first sync, adopt an existing POS product of the same name
        // (made before the integration) instead of creating a duplicate.
        var product = await _db.Products.Include(p => p.Variations)
                          .FirstOrDefaultAsync(p => p.ScmsProductId == scmsId)
                      ?? await _db.Products.Include(p => p.Variations)
                          .FirstOrDefaultAsync(p => p.ScmsProductId == null &&
                                                    p.ProductName.ToLower() == dto.ProductName.Trim().ToLower());
        if (product == null)
        {
            product = new Product();
            _db.Products.Add(product);
        }

        product.ScmsProductId = scmsId;
        product.ProductName = dto.ProductName.Trim();
        product.ProductCategory = dto.ProductCategory.Trim();
        product.ProductDescription = dto.ProductDescription;
        product.ProductImage = dto.ProductImage;
        product.IsActive = dto.IsActive;
        product.SyncedAt = now;

        var syncedVariations = new List<ProductVariation>();
        var suggestedPrices = new List<(ProductVariation Variation, decimal Price)>();
        foreach (var line in dto.Variations)
        {
            var sku = line.Sku.Trim();
            var name = string.IsNullOrWhiteSpace(line.VariationName)
                ? $"{sku}|{line.PackagingType.Trim()}|{line.Size.Trim()}"
                : line.VariationName.Trim();

            // Match by SKU anywhere in the catalog; otherwise a pre-integration variation of
            // this product whose name starts with the SKU ("SKU|Packaging|Size") gets the SKU.
            var variation = await _db.ProductVariations
                                .FirstOrDefaultAsync(v => v.Sku != null && v.Sku.ToLower() == sku.ToLower())
                            ?? product.Variations.FirstOrDefault(v =>
                                v.Sku == null && v.VariationName.StartsWith(sku + "|", StringComparison.OrdinalIgnoreCase));
            if (variation == null)
            {
                variation = new ProductVariation();
                product.Variations.Add(variation);
            }
            else if (variation.ProductId != product.ProductId && product.ProductId != 0)
            {
                variation.ProductId = product.ProductId; // SCMS moved this SKU to another product
            }

            variation.Sku = sku;
            variation.VariationName = name;
            variation.IsActive = line.IsActive;
            variation.SyncedAt = now;
            syncedVariations.Add(variation);
            if (line.SuggestedPrice is { } suggested) suggestedPrices.Add((variation, decimal.Round(suggested, 2)));
        }

        await _db.SaveChangesAsync();

        // Starting prices: only for variations with no real (> 0) POS price yet, so a
        // manager's price is never overwritten by a later sync.
        var startingPricesSet = 0;
        foreach (var (variation, suggested) in suggestedPrices)
        {
            var activePrices = await _db.ProductPrices
                .Where(pp => pp.VariationId == variation.VariationId && pp.IsActive)
                .ToListAsync();
            if (activePrices.Any(pp => pp.Price > 0)) continue;

            activePrices.ForEach(pp => pp.IsActive = false); // a leftover ₱0 placeholder
            _db.ProductPrices.Add(new ProductPrice
            {
                VariationId = variation.VariationId,
                Price = suggested,
                IsActive = true,
                EffectiveFrom = now,
                CreatedAt = now,
            });
            startingPricesSet++;
        }
        if (startingPricesSet > 0) await _db.SaveChangesAsync();

        var syncedIds = syncedVariations.Select(v => v.VariationId).ToList();
        var priced = await _db.ProductPrices
            .Where(pp => syncedIds.Contains(pp.VariationId) && pp.IsActive && pp.Price > 0)
            .Select(pp => pp.VariationId)
            .Distinct()
            .CountAsync();

        return new SyncProductResultDto
        {
            ProductId = product.ProductId,
            ScmsProductId = scmsId,
            SyncedVariationsCount = syncedVariations.Count,
            PricesReceivedCount = suggestedPrices.Count,
            StartingPricesSetCount = startingPricesSet,
            PendingPricingCount = syncedVariations.Count - priced,
        };
    }

    private static void ValidateSync(SyncProductDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ScmsProductId)) throw new InvalidOperationException("scmsProductId is required.");
        if (dto.ScmsProductId.Trim().Length > 100) throw new InvalidOperationException("scmsProductId must be 100 characters or fewer.");
        if (string.IsNullOrWhiteSpace(dto.ProductName)) throw new InvalidOperationException("productName is required.");
        if (dto.ProductName.Trim().Length > 150) throw new InvalidOperationException("productName must be 150 characters or fewer.");
        if (string.IsNullOrWhiteSpace(dto.ProductCategory)) throw new InvalidOperationException("productCategory is required.");
        if (dto.ProductCategory.Trim().Length > 100) throw new InvalidOperationException("productCategory must be 100 characters or fewer.");
        if (dto.ProductImage is { Length: > 255 }) throw new InvalidOperationException("productImage must be 255 characters or fewer.");
        if (dto.Variations is not { Count: > 0 }) throw new InvalidOperationException("variations must list at least one variation.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < dto.Variations.Count; i++)
        {
            var line = dto.Variations[i];
            if (string.IsNullOrWhiteSpace(line.Sku)) throw new InvalidOperationException($"variations[{i}].sku is required.");
            if (line.Sku.Trim().Length > 100) throw new InvalidOperationException($"variations[{i}].sku must be 100 characters or fewer.");
            if (!seen.Add(line.Sku.Trim())) throw new InvalidOperationException($"variations[{i}].sku '{line.Sku}' is listed twice.");
            if (string.IsNullOrWhiteSpace(line.PackagingType)) throw new InvalidOperationException($"variations[{i}].packagingType is required.");
            if (string.IsNullOrWhiteSpace(line.Size)) throw new InvalidOperationException($"variations[{i}].size is required.");
            var name = string.IsNullOrWhiteSpace(line.VariationName)
                ? $"{line.Sku.Trim()}|{line.PackagingType.Trim()}|{line.Size.Trim()}"
                : line.VariationName.Trim();
            if (name.Length > 100) throw new InvalidOperationException($"variations[{i}].variationName must be 100 characters or fewer.");
            if (line.SuggestedPrice is { } price && (price <= 0 || price > 1_000_000))
                throw new InvalidOperationException($"variations[{i}].price must be more than 0 and at most 1,000,000 (or left out).");
        }
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private static ProductResponseDto MapToProductResponse(Product product)
    {
        return new ProductResponseDto
        {
            ProductId = product.ProductId,
            ProductName = product.ProductName,
            ProductCategory = product.ProductCategory,
            ProductDescription = product.ProductDescription,
            ProductImage = product.ProductImage,
            IsActive = product.IsActive,
            Variations = product.Variations.Select(v => new VariationResponseDto
            {
                VariationId = v.VariationId,
                VariationName = v.VariationName,
                IsActive = v.IsActive,
                CurrentPrice = v.ProductPrices.FirstOrDefault(pp => pp.IsActive)?.Price
            }).ToList()
        };
    }
}
