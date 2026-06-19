using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Customer;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/customers")]
public class CustomerPortalController : ControllerBase
{
    private readonly ICustomerPortalService _customerPortalService;

    public CustomerPortalController(ICustomerPortalService customerPortalService)
    {
        _customerPortalService = customerPortalService;
    }

    // Resolve customer identity: prefer GUID auth ID, fall back to int header
    private (string? authId, int? customerId) GetCustomerIdentity()
    {
        // Try GUID-based auth ID first (set by frontend from user.id)
        if (Request.Headers.TryGetValue("X-Auth-Id", out var authIdVal) && !string.IsNullOrWhiteSpace(authIdVal))
            return (authIdVal.ToString(), null);

        // Fall back to integer customer ID for legacy/POS use
        if (Request.Headers.TryGetValue("X-Customer-Id", out var custIdStr) && int.TryParse(custIdStr, out int headerCustId))
            return (null, headerCustId);

        return (null, null);
    }

    [HttpGet("orders")]
    [ProducesResponseType(typeof(List<CustomerOrderHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrderHistory()
    {
        var (authId, customerId) = GetCustomerIdentity();

        if (authId != null)
        {
            var orders = await _customerPortalService.GetOrderHistoryByAuthIdAsync(authId);
            return Ok(orders);
        }

        if (customerId != null)
        {
            var orders = await _customerPortalService.GetOrderHistoryAsync(customerId.Value);
            return Ok(orders);
        }

        return Unauthorized("Customer identity not found in request headers.");
    }

    [HttpGet("orders/{orderId:int}/tracking")]
    [ProducesResponseType(typeof(OrderTrackingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderTracking(int orderId)
    {
        var (authId, customerId) = GetCustomerIdentity();

        OrderTrackingDto? tracking = null;

        if (authId != null)
            tracking = await _customerPortalService.GetOrderTrackingByAuthIdAsync(orderId, authId);
        else if (customerId != null)
            tracking = await _customerPortalService.GetOrderTrackingAsync(orderId, customerId.Value);
        else
            return Unauthorized("Customer identity not found in request headers.");

        if (tracking == null) return NotFound("Order not found or does not belong to this customer.");
        return Ok(tracking);
    }

    // EC-023: Request a refund for a delivered order
    // POST /api-pos/customers/orders/{orderId}/refund
    [HttpPost("orders/{orderId:int}/refund")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestRefund(int orderId, [FromBody] RefundRequestDto dto)
    {
        var (authId, customerId) = GetCustomerIdentity();
        if (authId == null && customerId == null)
            return Unauthorized("Customer identity not found in request headers.");

        var result = authId != null
            ? await _customerPortalService.RequestRefundByAuthIdAsync(orderId, authId, dto.Reason)
            : await _customerPortalService.RequestRefundAsync(orderId, customerId!.Value, dto.Reason);

        return result.Success ? Ok(new { message = result.Message }) : BadRequest(new { message = result.Message });
    }

    // EC-024: Get all refund requests for the current customer
    // GET /api-pos/customers/refunds
    [HttpGet("refunds")]
    [ProducesResponseType(typeof(List<CustomerRefundRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRefunds()
    {
        var (authId, customerId) = GetCustomerIdentity();

        if (authId != null)
        {
            var refunds = await _customerPortalService.GetRefundRequestsByAuthIdAsync(authId);
            return Ok(refunds);
        }

        if (customerId != null)
        {
            var refunds = await _customerPortalService.GetRefundRequestsAsync(customerId.Value);
            return Ok(refunds);
        }

        return Unauthorized("Customer identity not found in request headers.");
    }
}
