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
}
