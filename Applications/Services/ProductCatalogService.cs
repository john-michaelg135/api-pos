using Applications.Audit;
using Applications.Interfaces;
using Api.Contracts.ProductCatalog;
using Api.Middlewares;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class ProductCatalogService : IProductCatalogService
{
    private readonly PosDbContext _db;
    private readonly IAuditLogService _auditLogService;
    private readonly IPosStaffIdentity? _staff;

    public ProductCatalogService(PosDbContext db, IAuditLogService auditLogService, IPosStaffIdentity? staff = null)
    {
        _db = db;
        _auditLogService = auditLogService;
        _staff = staff;
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

        await RecordAsync(AuditActions.Created, ProductTarget(product), who => $"{who} created product {product.ProductName}.",
            DateTime.UtcNow, $"product-created:{product.ProductId}",
            new() { ["operation"] = "product_created", ["product_id"] = product.ProductId });

        return MapToProductResponse(product);
    }

    public async Task<ProductResponseDto?> UpdateProductAsync(int productId, UpdateProductDto dto)
    {
        var product = await _db.Products
            .Include(p => p.Variations)
                .ThenInclude(v => v.ProductPrices.Where(pp => pp.IsActive))
            .FirstOrDefaultAsync(p => p.ProductId == productId);

        if (product == null) return null;

        var changed = new List<string>();
        if (dto.ProductName != null && dto.ProductName != product.ProductName) { product.ProductName = dto.ProductName; changed.Add("name"); }
        if (dto.ProductCategory != null && dto.ProductCategory != product.ProductCategory) { product.ProductCategory = dto.ProductCategory; changed.Add("category"); }
        if (dto.ProductDescription != null && dto.ProductDescription != product.ProductDescription) { product.ProductDescription = dto.ProductDescription; changed.Add("description"); }
        if (dto.ProductImage != null && dto.ProductImage != product.ProductImage) { product.ProductImage = dto.ProductImage; changed.Add("image"); }
        if (dto.IsActive.HasValue && dto.IsActive.Value != product.IsActive) { product.IsActive = dto.IsActive.Value; changed.Add("active"); }

        _db.Products.Update(product);
        await _db.SaveChangesAsync();

        // An edit has no saved revision, so each submission is its own occurrence (operationId null).
        if (changed.Count > 0)
        {
            await RecordAsync(AuditActions.Updated, ProductTarget(product),
                who => $"{who} edited product {product.ProductName} ({string.Join(", ", changed)}).",
                DateTime.UtcNow, operationId: null,
                new() { ["operation"] = "product_updated", ["product_id"] = product.ProductId, ["changed_fields"] = changed });
        }

        return MapToProductResponse(product);
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

        await RecordAsync(AuditActions.Created, VariationTarget(variation, product.ProductName),
            who => $"{who} added variation {VariationLabel(variation.VariationName)} to {product.ProductName} at ₱{dto.InitialPrice:N2}.",
            now, $"variation-created:{variation.VariationId}",
            new()
            {
                ["operation"] = "variation_created",
                ["product_id"] = productId,
                ["variation_id"] = variation.VariationId,
                ["new_price"] = dto.InitialPrice,
            });

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

        var changed = new List<string>();
        if (dto.VariationName != null && dto.VariationName != variation.VariationName) { variation.VariationName = dto.VariationName; changed.Add("name"); }
        if (dto.IsActive.HasValue && dto.IsActive.Value != variation.IsActive) { variation.IsActive = dto.IsActive.Value; changed.Add("active"); }

        _db.ProductVariations.Update(variation);
        await _db.SaveChangesAsync();

        if (changed.Count > 0)
        {
            var productName = await ProductNameAsync(variation.ProductId);
            await RecordAsync(AuditActions.Updated, VariationTarget(variation, productName),
                who => $"{who} edited variation {VariationLabel(variation.VariationName)} of {productName} ({string.Join(", ", changed)}).",
                DateTime.UtcNow, operationId: null,
                new()
                {
                    ["operation"] = "variation_updated",
                    ["product_id"] = variation.ProductId,
                    ["variation_id"] = variation.VariationId,
                    ["changed_fields"] = changed,
                });
        }

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
            .Include(v => v.Product)
            .Include(v => v.ProductPrices)
            .FirstOrDefaultAsync(v => v.VariationId == variationId);

        if (variation == null) return null;

        var now = DateTime.UtcNow;

        if (dto.ProductImage != null) variation.Product.PosImage = dto.ProductImage;

        // Find current active price and archive it
        var currentActivePrice = variation.ProductPrices.FirstOrDefault(pp => pp.IsActive);

        // Saving the same price again is not a change; keep the history free of no-op entries.
        if (currentActivePrice != null && currentActivePrice.Price == dto.Price)
        {
            if (dto.ProductImage != null) await _db.SaveChangesAsync();
            return new VariationResponseDto
            {
                VariationId = variation.VariationId,
                VariationName = variation.VariationName,
                IsActive = variation.IsActive,
                CurrentPrice = currentActivePrice.Price
            };
        }

        // Who changed it is the signed-in staff member, not a number the client sends.
        var setBy = (_staff is null ? null : await _staff.CurrentStaffIdAsync()) ?? dto.SetBy;

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
            SetBy = setBy,
            EffectiveFrom = now,
            CreatedAt = now
        };

        await _db.ProductPrices.AddAsync(newPrice);
        await _db.SaveChangesAsync();

        // Each saved price row is its own occurrence, so two edits of one price never collapse.
        var productName = await ProductNameAsync(variation.ProductId);
        var oldPrice = currentActivePrice?.Price;
        await RecordAsync(AuditActions.Updated, VariationTarget(variation, productName),
            who => oldPrice.HasValue
                ? $"{who} changed the price of {productName} {VariationLabel(variation.VariationName)} from ₱{oldPrice.Value:N2} to ₱{dto.Price:N2}."
                : $"{who} set the price of {productName} {VariationLabel(variation.VariationName)} to ₱{dto.Price:N2}.",
            newPrice.CreatedAt, $"price:{newPrice.PriceId}",
            new()
            {
                ["operation"] = "price_set",
                ["product_id"] = variation.ProductId,
                ["variation_id"] = variation.VariationId,
                ["old_price"] = oldPrice,
                ["new_price"] = dto.Price,
            });

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

    /// <summary>
    /// Every price each variation has had, from the saved price rows (a price change keeps the old
    /// row and adds a new one). ₱0 placeholders for unpriced variations are left out. Newest first.
    /// </summary>
    public async Task<List<PriceChangeDto>> GetPriceChangesAsync(
        int? productId,
        int? variationId,
        IReadOnlyCollection<int>? variationIds = null)
    {
        var query = _db.ProductPrices
            .AsNoTracking()
            .Where(pp => pp.Price > 0);
        if (variationId.HasValue) query = query.Where(pp => pp.VariationId == variationId.Value);
        if (productId.HasValue) query = query.Where(pp => pp.ProductVariation.ProductId == productId.Value);
        if (variationIds is not null) query = query.Where(pp => variationIds.Contains(pp.VariationId));

        var rows = await query
            .Select(pp => new
            {
                pp.PriceId,
                pp.VariationId,
                pp.Price,
                pp.IsActive,
                pp.SetBy,
                pp.EffectiveFrom,
                pp.ProductVariation.ProductId,
                pp.ProductVariation.VariationName,
                ProductName = pp.ProductVariation.Product.ProductName,
            })
            .ToListAsync();

        var staffIds = rows.Where(r => r.SetBy is > 0).Select(r => r.SetBy!.Value).Distinct().ToArray();
        var names = _staff is null || staffIds.Length == 0
            ? new Dictionary<int, string>()
            : await _staff.NamesAsync(staffIds);

        var changes = new List<PriceChangeDto>();
        foreach (var timeline in rows.GroupBy(r => r.VariationId))
        {
            var ordered = timeline.OrderBy(r => r.EffectiveFrom).ThenBy(r => r.PriceId).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                decimal? previous = i > 0 ? ordered[i - 1].Price : null;
                var next = i + 1 < ordered.Count ? ordered[i + 1] : null;
                changes.Add(new PriceChangeDto
                {
                    PriceId = row.PriceId,
                    ProductId = row.ProductId,
                    ProductName = row.ProductName,
                    VariationId = row.VariationId,
                    VariationName = row.VariationName,
                    Price = row.Price,
                    PreviousPrice = previous,
                    Change = previous.HasValue ? row.Price - previous.Value : null,
                    ChangePercent = previous is > 0
                        ? Math.Round((row.Price - previous.Value) / previous.Value * 100m, 2)
                        : null,
                    EffectiveFrom = row.EffectiveFrom,
                    EffectiveTo = next?.EffectiveFrom,
                    IsCurrent = row.IsActive && next is null,
                    SetBy = row.SetBy,
                    SetByName = row.SetBy is { } by ? names.GetValueOrDefault(by) : null,
                });
            }
        }

        return changes
            .OrderByDescending(c => c.EffectiveFrom)
            .ThenByDescending(c => c.PriceId)
            .ToList();
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

    private Task RecordAsync(
        string action,
        AuditTarget target,
        Func<string, string> describe,
        DateTime occurredAt,
        string? operationId,
        Dictionary<string, object?> metadata) =>
        _auditLogService.RecordAsync(new AuditEvent
        {
            Module = PosModules.ProductManagement,
            Action = action,
            Target = target,
            Describe = describe,
            OccurredAt = occurredAt,
            OperationId = operationId,
            Metadata = metadata,
        });

    private static AuditTarget ProductTarget(Product product) =>
        new("product", product.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture), product.ProductName);

    private static AuditTarget VariationTarget(ProductVariation variation, string? productName) =>
        new("product_variation",
            variation.VariationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{productName ?? "Product"} {VariationLabel(variation.VariationName)}".Trim());

    private async Task<string?> ProductNameAsync(int productId) =>
        await _db.Products.AsNoTracking().Where(p => p.ProductId == productId).Select(p => p.ProductName).FirstOrDefaultAsync();

    // "Jar · 300g" from "UJM-SM-300|Jar|300g"; names without parts stay as they are.
    private static string VariationLabel(string? variationName)
    {
        if (string.IsNullOrWhiteSpace(variationName)) return string.Empty;
        if (!variationName.Contains('|')) return variationName;
        return string.Join(" · ", variationName.Split('|').Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0));
    }

    private static ProductResponseDto MapToProductResponse(Product product)
    {
        return new ProductResponseDto
        {
            ProductId = product.ProductId,
            ProductName = product.ProductName,
            ProductCategory = product.ProductCategory,
            ProductDescription = product.ProductDescription,
            ProductImage = product.PosImage ?? product.ProductImage,
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
