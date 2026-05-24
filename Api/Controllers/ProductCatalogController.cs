using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.ProductCatalog;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/product-catalog")]
public class ProductCatalogController : ControllerBase
{
    private readonly IProductCatalogService _productCatalogService;

    public ProductCatalogController(IProductCatalogService productCatalogService)
    {
        _productCatalogService = productCatalogService;
    }

    // ── POS-001: Product CRUD ──

    // GET api-pos/product-catalog/products
    [HttpGet("products")]
    [ProducesResponseType(typeof(List<ProductResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllProducts()
    {
        var products = await _productCatalogService.GetAllProductsAsync();
        return Ok(products);
    }

    // GET api-pos/product-catalog/products/{productId}
    [HttpGet("products/{productId:int}")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProductById(int productId)
    {
        var product = await _productCatalogService.GetProductByIdAsync(productId);
        if (product == null) return NotFound("Product not found.");
        return Ok(product);
    }

    // POST api-pos/product-catalog/products
    [HttpPost("products")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductDto dto)
    {
        if (dto == null) return BadRequest("Product data is required.");
        if (string.IsNullOrWhiteSpace(dto.ProductName)) return BadRequest("Product name is required.");
        if (string.IsNullOrWhiteSpace(dto.ProductCategory)) return BadRequest("Product category is required.");

        var product = await _productCatalogService.CreateProductAsync(dto);
        return CreatedAtAction(nameof(GetProductById), new { productId = product.ProductId }, product);
    }

    // PUT api-pos/product-catalog/products/{productId}
    [HttpPut("products/{productId:int}")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateProduct(int productId, [FromBody] UpdateProductDto dto)
    {
        if (dto == null) return BadRequest("Update data is required.");

        var product = await _productCatalogService.UpdateProductAsync(productId, dto);
        if (product == null) return NotFound("Product not found.");
        return Ok(product);
    }

    // ── POS-002: Variation management ──

    // POST api-pos/product-catalog/products/{productId}/variations
    [HttpPost("products/{productId:int}/variations")]
    [ProducesResponseType(typeof(VariationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateVariation(int productId, [FromBody] CreateVariationDto dto)
    {
        if (dto == null) return BadRequest("Variation data is required.");
        if (string.IsNullOrWhiteSpace(dto.VariationName)) return BadRequest("Variation name is required.");
        if (dto.InitialPrice <= 0) return BadRequest("Initial price must be greater than zero.");

        var variation = await _productCatalogService.CreateVariationAsync(productId, dto);
        if (variation == null) return NotFound("Product not found.");
        return CreatedAtAction(nameof(GetProductById), new { productId }, variation);
    }

    // PUT api-pos/product-catalog/variations/{variationId}
    [HttpPut("variations/{variationId:int}")]
    [ProducesResponseType(typeof(VariationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateVariation(int variationId, [FromBody] UpdateVariationDto dto)
    {
        if (dto == null) return BadRequest("Update data is required.");

        var variation = await _productCatalogService.UpdateVariationAsync(variationId, dto);
        if (variation == null) return NotFound("Variation not found.");
        return Ok(variation);
    }

    // ── POS-003: Price management ──

    // PUT api-pos/product-catalog/variations/{variationId}/price
    [HttpPut("variations/{variationId:int}/price")]
    [ProducesResponseType(typeof(VariationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetVariationPrice(int variationId, [FromBody] SetPriceDto dto)
    {
        if (dto == null) return BadRequest("Price data is required.");
        if (dto.Price <= 0) return BadRequest("Price must be greater than zero.");

        var variation = await _productCatalogService.SetVariationPriceAsync(variationId, dto);
        if (variation == null) return NotFound("Variation not found.");
        return Ok(variation);
    }

    // ── POS-004: Price history ──

    // GET api-pos/product-catalog/variations/{variationId}/price-history
    [HttpGet("variations/{variationId:int}/price-history")]
    [ProducesResponseType(typeof(List<PriceHistoryResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPriceHistory(int variationId)
    {
        var history = await _productCatalogService.GetPriceHistoryAsync(variationId);
        return Ok(history);
    }
}
