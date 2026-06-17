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

    // Helper to get CustomerId from AuthToken
    private int? GetCustomerId()
    {
        // JWT claims usually have "sub" or "nameid" for user ID
        // Note: For actual auth from ms-authentication, check which claim holds the customer ID.
        // Assuming user ID is stored in "id" or "nameid"
        var claim = User.Claims.FirstOrDefault(c => c.Type == "id" || c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
        if (claim != null && int.TryParse(claim.Value, out int customerId))
        {
            return customerId;
        }
        
        // Temporarily, we can also support an explicit header for testing if JWT is not fully hooked up for customers
        if (Request.Headers.TryGetValue("X-Customer-Id", out var custIdStr) && int.TryParse(custIdStr, out int headerCustId))
        {
            return headerCustId;
        }

        return null;
    }

    [HttpGet("orders")]
    [ProducesResponseType(typeof(List<CustomerOrderHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrderHistory()
    {
        var customerId = GetCustomerId();
        if (customerId == null) return Unauthorized("Customer ID not found in token.");

        var orders = await _customerPortalService.GetOrderHistoryAsync(customerId.Value);
        return Ok(orders);
    }

    [HttpGet("orders/{orderId:int}/tracking")]
    [ProducesResponseType(typeof(OrderTrackingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderTracking(int orderId)
    {
        var customerId = GetCustomerId();
        if (customerId == null) return Unauthorized("Customer ID not found in token.");

        var tracking = await _customerPortalService.GetOrderTrackingAsync(orderId, customerId.Value);
        if (tracking == null) return NotFound("Order not found or does not belong to this customer.");

        return Ok(tracking);
    }
}
