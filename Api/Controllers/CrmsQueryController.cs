using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Crms;

namespace Api.Controllers;

/// <summary>
/// Read-only endpoints that the CRMS microservice calls to retrieve
/// customer order history and cart data from POS.
/// </summary>
[ApiController]
public class CrmsQueryController : ControllerBase
{
    private readonly ICrmsQueryService _crmsService;

    public CrmsQueryController(ICrmsQueryService crmsService)
    {
        _crmsService = crmsService;
    }

    // ── CRMS-POS-001: Get customer order history with filters ──

    // GET api-pos/customers/{customerId}/orders
    [HttpGet("api-pos/customers/{customerId}/orders")]
    [ProducesResponseType(typeof(CrmsCustomerOrdersResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCustomerOrders(string customerId, [FromQuery] CrmsOrderFilterDto filter)
    {
        var result = await _crmsService.GetCustomerOrdersAsync(customerId, filter ?? new CrmsOrderFilterDto());
        return Ok(result);
    }

    // ── CRMS-POS-002: Get full details of a single order ──

    // GET api-pos/orders/{orderId}
    [HttpGet("api-pos/orders/{orderId:int}")]
    [ProducesResponseType(typeof(CrmsOrderDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderDetail(int orderId)
    {
        var order = await _crmsService.GetOrderDetailAsync(orderId);
        if (order == null) return NotFound($"Order with ID {orderId} not found.");
        return Ok(order);
    }

    // ── CRMS-POS-003: Get customer's current cart ──

    // GET api-pos/customers/{customerId}/cart
    [HttpGet("api-pos/customers/{customerId}/cart")]
    [ProducesResponseType(typeof(CrmsCartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomerCart(string customerId)
    {
        var cart = await _crmsService.GetCustomerCartAsync(customerId);
        if (cart == null) return NotFound($"No active cart found for customer '{customerId}'.");
        return Ok(cart);
    }
}
