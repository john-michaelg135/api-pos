using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Inventory;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/inventory")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventoryService;

    public InventoryController(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    // ── POS-010: Stock receiving from SCMS ──

    // POST api-pos/inventory/stock-receiving
    [HttpPost("stock-receiving")]
    [ProducesResponseType(typeof(StockResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveStock([FromBody] StockReceivingDto dto)
    {
        if (dto == null) return BadRequest("Stock receiving data is required.");
        if (dto.QuantityReceived <= 0) return BadRequest("Quantity received must be greater than zero.");

        try
        {
            var stock = await _inventoryService.ReceiveStockAsync(dto);
            return CreatedAtAction(nameof(GetStockByLocation), new { locationId = dto.LocationId }, stock);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // GET api-pos/inventory/stock-receiving
    [HttpGet("stock-receiving")]
    [ProducesResponseType(typeof(List<StockReceivingResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStockReceivingHistory()
    {
        var history = await _inventoryService.GetStockReceivingHistoryAsync();
        return Ok(history);
    }

    // ── POS-012: View stock per location ──

    // GET api-pos/inventory/stock?locationId=X
    [HttpGet("stock")]
    [ProducesResponseType(typeof(List<StockResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStockByLocation([FromQuery] int locationId)
    {
        if (locationId <= 0) return BadRequest("A valid locationId is required.");
        var stock = await _inventoryService.GetStockByLocationAsync(locationId);
        return Ok(stock);
    }

    // ── POS-012 (extended): View all stock across all locations ──

    // GET api-pos/inventory/stock/all
    [HttpGet("stock/all")]
    [ProducesResponseType(typeof(List<StockResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllStock()
    {
        var stock = await _inventoryService.GetAllStockAsync();
        return Ok(stock);
    }

    // ── POS-018: Low-stock threshold alerts ──

    // GET api-pos/inventory/low-stock
    [HttpGet("low-stock")]
    [ProducesResponseType(typeof(List<LowStockAlertDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLowStockAlerts()
    {
        var alerts = await _inventoryService.GetLowStockAlertsAsync();
        return Ok(alerts);
    }

}
