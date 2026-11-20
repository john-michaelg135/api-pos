using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.OrderManagement;
using Api.Middlewares;

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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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

    // ── Institutional delivery confirmation with proof ──

    // PUT api-pos/order-management/orders/{orderId}/confirm-institutional-delivery
    [HttpPut("orders/{orderId:int}/confirm-institutional-delivery")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmInstitutionalDelivery(int orderId, [FromBody] ConfirmInstitutionalDeliveryDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Delivery details are required.");
        if (string.IsNullOrWhiteSpace(dto.ReceivedBy)) return BadRequest("Received by is required.");
        try
        {
            var order = await _orderManagementService.ConfirmInstitutionalDeliveryAsync(orderId, dto);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // GET api-pos/order-management/orders/{orderId}/delivery-proof
    // A stored File answers with a fresh five-minute signed address; call again when it expires.
    [HttpGet("orders/{orderId:int}/delivery-proof")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetDeliveryProof(int orderId) =>
        ProofImageAsync(() => _orderManagementService.GetDeliveryProofImageAsync(orderId), "No proof photo for this order.");

    // ── Proof photo uploads (shared file-storage service) ──
    // 1. POST .../proof-uploads/{kind}         → signed upload address + pending reference
    // 2. Browser PUTs the JPEG straight to the signed address (no credentials)
    // 3. POST .../proof-uploads/{kind}/confirm → ready reference, sent as proofReference with the save

    // POST api-pos/order-management/orders/{orderId}/proof-uploads/{kind}   (kind: delivery | refund)
    [HttpPost("orders/{orderId:int}/proof-uploads/{kind}")]
    [RateLimit(WindowSeconds = 10, MaxRequests = 5, Message = "Too many photo uploads. Wait a moment and try again.")]
    [ProducesResponseType(typeof(ProofUploadStartedDto), StatusCodes.Status201Created)]
    public Task<IActionResult> StartProofUpload(int orderId, string kind, [FromBody] ProofUploadRequestDto dto,
        [FromServices] Applications.ProofFiles.IProofUploadService uploads) =>
        ProofUploadAsync(async () => StatusCode(StatusCodes.Status201Created,
            await uploads.StartAsync(orderId, kind, dto ?? new ProofUploadRequestDto(), HttpContext.RequestAborted)));

    // POST api-pos/order-management/orders/{orderId}/proof-uploads/{kind}/confirm
    [HttpPost("orders/{orderId:int}/proof-uploads/{kind}/confirm")]
    [RateLimit(WindowSeconds = 10, MaxRequests = 5, Message = "Too many photo confirmations. Wait a moment and try again.")]
    [ProducesResponseType(typeof(ProofUploadReadyDto), StatusCodes.Status200OK)]
    public Task<IActionResult> ConfirmProofUpload(int orderId, string kind, [FromBody] ProofUploadConfirmDto dto,
        [FromServices] Applications.ProofFiles.IProofUploadService uploads) =>
        ProofUploadAsync(async () => Ok(await uploads.ConfirmAsync(orderId, kind, dto?.PendingReference, HttpContext.RequestAborted)));

    private async Task<IActionResult> ProofUploadAsync(Func<Task<IActionResult>> run)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return await run();
        }
        catch (Applications.ProofFiles.ProofUploadException ex)
        {
            return StatusCode(ex.Status, new { message = ex.Message, cleanupFailed = ex.CleanupFailed });
        }
    }

    private async Task<IActionResult> ProofImageAsync(Func<Task<ProofImageResult?>> load, string missing)
    {
        // Signed addresses and photos must not be cached by the browser or a proxy.
        Response.Headers.CacheControl = "no-store";
        try
        {
            var image = await load();
            if (image == null) return NotFound(missing);
            return Ok(new { proofImage = image.ProofImage, source = image.Source, pending = image.Pending });
        }
        catch (ProofImageUnavailableException ex)
        {
            return StatusCode(ex.Status, new { message = ex.Message, temporary = ex.Status == StatusCodes.Status503ServiceUnavailable });
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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

    // ── Institutional GCash downpayment: confirm remaining balance ──

    // PUT api-pos/order-management/orders/{orderId}/complete-payment
    [HttpPut("orders/{orderId:int}/complete-payment")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompletePayment(int orderId, [FromQuery] int confirmedBy = 1)
    {
        if (PosRoleAuthorization.RequireCashierOrManager(HttpContext) is { } denied) return denied;
        try
        {
            var order = await _orderManagementService.CompletePaymentAsync(orderId, confirmedBy);
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
        if (PosRoleAuthorization.RequireCashierOrManager(HttpContext) is { } denied) return denied;
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest("Refund reason is required.");
        try
        {
            var order = await _orderManagementService.RequestRefundAsync(orderId, dto.Reason, dto.ProofImage, dto.Items, dto.ProofReference);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // GET api-pos/order-management/orders/{orderId}/refund-proof
    [HttpGet("orders/{orderId:int}/refund-proof")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> GetRefundProof(int orderId) =>
        ProofImageAsync(() => _orderManagementService.GetRefundProofImageAsync(orderId), "No refund photo for this order.");

    // PUT api-pos/order-management/orders/{orderId}/approve-refund
    [HttpPut("orders/{orderId:int}/approve-refund")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveRefund(int orderId, [FromBody] ApproveOrderDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Approval data is required.");
        if (string.IsNullOrWhiteSpace(dto.PinCode)) return BadRequest("Manager PIN is required.");
        try
        {
            var order = await _orderManagementService.ApproveRefundAsync(orderId, dto.ApprovedBy, dto.PinCode);
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Rejection data is required.");
        if (string.IsNullOrWhiteSpace(dto.RejectionRemarks)) return BadRequest("Rejection remarks are required.");
        if (string.IsNullOrWhiteSpace(dto.PinCode)) return BadRequest("Manager PIN is required.");
        try
        {
            var order = await _orderManagementService.RejectRefundAsync(orderId, dto.RejectedBy, dto.RejectionRemarks, dto.PinCode);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── POS-xxx: Voids ──

    // PUT api-pos/order-management/orders/{orderId}/request-void
    [HttpPut("orders/{orderId:int}/request-void")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestVoid(int orderId, [FromBody] RequestRefundDto dto)
    {
        if (PosRoleAuthorization.RequireCashierOrManager(HttpContext) is { } denied) return denied;
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason)) return BadRequest("Void reason is required.");
        try
        {
            var order = await _orderManagementService.RequestVoidAsync(orderId, dto.Reason);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/approve-void
    [HttpPut("orders/{orderId:int}/approve-void")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveVoid(int orderId, [FromBody] ApproveOrderDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Approval data is required.");
        try
        {
            var order = await _orderManagementService.ApproveVoidAsync(orderId, dto.ApprovedBy, dto.PinCode);
            if (order == null) return NotFound($"Order with ID {orderId} not found.");
            return Ok(order);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/order-management/orders/{orderId}/reject-void
    [HttpPut("orders/{orderId:int}/reject-void")]
    [ProducesResponseType(typeof(OrderManagementResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectVoid(int orderId, [FromBody] RejectOrderDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Rejection data is required.");
        if (string.IsNullOrWhiteSpace(dto.RejectionRemarks)) return BadRequest("Rejection remarks are required.");
        if (string.IsNullOrWhiteSpace(dto.PinCode)) return BadRequest("Manager PIN is required.");
        try
        {
            var order = await _orderManagementService.RejectVoidAsync(orderId, dto.RejectedBy, dto.RejectionRemarks, dto.PinCode);
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
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
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
