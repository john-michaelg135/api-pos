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
