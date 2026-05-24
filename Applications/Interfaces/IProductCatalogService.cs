using Api.Contracts.ProductCatalog;

namespace Applications.Interfaces;

public interface IProductCatalogService
{
    // POS-001: Product CRUD
    Task<ProductResponseDto> CreateProductAsync(CreateProductDto dto);
    Task<ProductResponseDto?> UpdateProductAsync(int productId, UpdateProductDto dto);
    Task<List<ProductResponseDto>> GetAllProductsAsync();
    Task<ProductResponseDto?> GetProductByIdAsync(int productId);

    // POS-002: Variation management
    Task<VariationResponseDto?> CreateVariationAsync(int productId, CreateVariationDto dto);
    Task<VariationResponseDto?> UpdateVariationAsync(int variationId, UpdateVariationDto dto);

    // POS-003: Price management (auto-creates price history)
    Task<VariationResponseDto?> SetVariationPriceAsync(int variationId, SetPriceDto dto);

    // POS-004: Price history
    Task<List<PriceHistoryResponseDto>> GetPriceHistoryAsync(int variationId);
}
