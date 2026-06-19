using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.OrderEntry;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/order-entry")]
public class OrderEntryController : ControllerBase
{
    private readonly IOrderEntryService _orderEntryService;

    public OrderEntryController(IOrderEntryService orderEntryService)
    {
        _orderEntryService = orderEntryService;
    }

    // ── POS-005 / EC-004: Product grid with optional stock availability ──

    // GET api-pos/order-entry/product-grid?locationId=X
    [HttpGet("product-grid")]
    [ProducesResponseType(typeof(List<ProductGridItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProductGrid([FromQuery] int? locationId = null)
    {
        var gridItems = await _orderEntryService.GetProductGridAsync(locationId);
        return Ok(gridItems);
    }

    // ── POS-006: Order creation (walk-in) ──

    // POST api-pos/order-entry/orders
    [HttpPost("orders")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderDto dto)
    {
        if (dto == null) return BadRequest("Order data is required.");
        if (dto.Items == null || dto.Items.Count == 0) return BadRequest("Order must have at least one item.");

        try
        {
            var order = await _orderEntryService.CreateOrderAsync(dto);
            return CreatedAtAction(nameof(GetOrderById), new { orderId = order.OrderId }, order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── POS-007: Walk-in order confirm ──

    // PUT api-pos/order-entry/orders/{orderId}/confirm
    [HttpPut("orders/{orderId:int}/confirm")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmOrder(int orderId, [FromBody] ConfirmOrderDto dto)
    {
        if (dto == null) return BadRequest("Confirmation data is required.");
        try
        {
            var order = await _orderEntryService.ConfirmOrderAsync(orderId, dto);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── POS-008: Institutional orders ──

    // POST api-pos/order-entry/orders/institutional
    [HttpPost("orders/institutional")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateInstitutionalOrder([FromBody] CreateInstitutionalOrderDto dto)
    {
        if (dto == null) return BadRequest("Order data is required.");
        if (dto.Items == null || dto.Items.Count == 0) return BadRequest("Order must have at least one item.");
        if (string.IsNullOrWhiteSpace(dto.DeliveryAddress)) return BadRequest("Delivery address is required for institutional orders.");

        try
        {
            var order = await _orderEntryService.CreateInstitutionalOrderAsync(dto);
            return CreatedAtAction(nameof(GetOrderById), new { orderId = order.OrderId }, order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── EC-008: Ecommerce order submission ──

    // POST api-pos/order-entry/orders/ecommerce
    [HttpPost("orders/ecommerce")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEcommerceOrder([FromBody] CreateEcommerceOrderDto dto)
    {
        if (dto == null) return BadRequest("Order data is required.");
        if (dto.Items == null || dto.Items.Count == 0) return BadRequest("Order must have at least one item.");

        try
        {
            var order = await _orderEntryService.CreateEcommerceOrderAsync(dto);
            return CreatedAtAction(nameof(GetOrderById), new { orderId = order.OrderId }, order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }


    // GET api-pos/order-entry/orders
    [HttpGet("orders")]
    [ProducesResponseType(typeof(List<OrderResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllOrders()
    {
        var orders = await _orderEntryService.GetAllOrdersAsync();
        return Ok(orders);
    }

    // GET api-pos/order-entry/orders/{orderId}
    [HttpGet("orders/{orderId:int}")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderById(int orderId)
    {
        var order = await _orderEntryService.GetOrderByIdAsync(orderId);
        if (order == null) return NotFound("Order not found.");
        return Ok(order);
    }
}
