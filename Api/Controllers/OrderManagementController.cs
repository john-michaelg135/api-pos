using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.OrderManagement;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/order-management")]
public class OrderManagementController : ControllerBase
{
    private readonly IOrderManagementService _orderManagementService;

    public OrderManagementController(IOrderManagementService orderManagementService)
    {
        _orderManagementService = orderManagementService;
    }

    // ── POS-013: Online order approval queue ──

    // GET api-pos/order-management/pending-approval
    [HttpGet("pending-approval")]
    [ProducesResponseType(typeof(List<OrderManagementResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingApproval()
    {
        var orders = await _orderManagementService.GetPendingApprovalAsync();
        return Ok(orders);
    }

    // PUT api-pos/order-management/orders/{orderId}/approve
    [HttpPut("orders/{orderId:int}/approve")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveOrder(int orderId, [FromBody] ApproveOrderDto dto)
    {
        if (dto == null) return BadRequest("Approval data is required.");
        try
        {
            var order = await _orderManagementService.ApproveOrderAsync(orderId, dto);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/reject
    [HttpPut("orders/{orderId:int}/reject")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectOrder(int orderId, [FromBody] RejectOrderDto dto)
    {
        if (dto == null) return BadRequest("Rejection data is required.");
        if (string.IsNullOrWhiteSpace(dto.RejectionRemarks)) return BadRequest("Rejection remarks are required.");
        try
        {
            var order = await _orderManagementService.RejectOrderAsync(orderId, dto);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── POS-014: Unified order list with filters ──

    // GET api-pos/order-management/orders
    [HttpGet("orders")]
    [ProducesResponseType(typeof(List<OrderManagementResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllOrders([FromQuery] OrderFilterDto filter)
    {
        var orders = await _orderManagementService.GetAllOrdersAsync(filter);
        return Ok(orders);
    }

    // ── POS-015: COD delivery confirmation ──

    // PUT api-pos/order-management/orders/{orderId}/confirm-delivery
    [HttpPut("orders/{orderId:int}/confirm-delivery")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmDelivery(int orderId, [FromQuery] int confirmedBy)
    {
        try
        {
            var order = await _orderManagementService.ConfirmDeliveryAsync(orderId, confirmedBy);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/status
    [HttpPut("orders/{orderId:int}/status")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateOrderStatus(int orderId, [FromBody] UpdateOrderStatusDto dto)
    {
        if (dto == null) return BadRequest("Status data is required.");
        try
        {
            var order = await _orderManagementService.UpdateOrderStatusAsync(orderId, dto);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── EC-012: Order tracking for progress bar ──

    // GET api-pos/order-management/orders/{orderId}/tracking
    [HttpGet("orders/{orderId:int}/tracking")]
    [ProducesResponseType(typeof(OrderTrackingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderTracking(int orderId)
    {
        var tracking = await _orderManagementService.GetOrderTrackingAsync(orderId);
        if (tracking == null) return NotFound($"Order with ID {orderId} not found.");
        return Ok(tracking);
    }

    // ── POS-016 & 017: Refunds ──

    // PUT api-pos/order-management/orders/{orderId}/request-refund
    [HttpPut("orders/{orderId:int}/request-refund")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestRefund(int orderId, [FromBody] RequestRefundDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest("Refund reason is required.");
        try
        {
            var order = await _orderManagementService.RequestRefundAsync(orderId, dto.Reason);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/approve-refund
    [HttpPut("orders/{orderId:int}/approve-refund")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveRefund(int orderId, [FromBody] ApproveOrderDto dto)
    {
        if (dto == null) return BadRequest("Approval data is required.");
        try
        {
            var order = await _orderManagementService.ApproveRefundAsync(orderId, dto.ApprovedBy);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/reject-refund
    [HttpPut("orders/{orderId:int}/reject-refund")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectRefund(int orderId, [FromBody] RejectOrderDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.RejectionRemarks)) return BadRequest("Rejection remarks are required.");
        try
        {
            var order = await _orderManagementService.RejectRefundAsync(orderId, dto.RejectedBy, dto.RejectionRemarks);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("sync-refunds")]
    public async Task<IActionResult> SyncRefunds([FromServices] Infrastructures.Persistence.PosDbContext db)
    {
        var refundedOrders = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Orders.Where(o => o.OrderStatus == "Refunded").Select(o => o.OrderId));
        
        var requests = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.RefundRequests.Where(r => refundedOrders.Contains(r.OrderId) && r.Status == "Pending"));
            
        foreach (var req in requests)
        {
            req.Status = "Approved";
            req.ApprovedAt = DateTime.UtcNow;
            db.RefundRequests.Update(req);
        }
        await db.SaveChangesAsync();
        return Ok($"Updated {requests.Count} refund requests.");
    }
}
