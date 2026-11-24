using Api.Contracts.StockRequests;
using Api.Middlewares;
using Applications.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Branch stock requests to IMS (STOCK-REQUEST-INTEGRATION.md §3.2, §3.5). Branch managers create
/// and list them; only the IMS service identity (SCMS_SERVICE_KEY) may record a decision.
/// </summary>
[ApiController]
[Route("api-pos/stock-requests")]
public class StockRequestsController : ControllerBase
{
    public const string ServiceRole = "SCMS Service";

    private readonly StockRequestService _requests;

    public StockRequestsController(StockRequestService requests)
    {
        _requests = requests;
    }

    // POST api-pos/stock-requests
    [HttpPost]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "This request is already being sent. Please wait a moment.")]
    [ProducesResponseType(typeof(StockRequestResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateStockRequestDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto is null) return Fail(400, "Stock request data is required.");
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, dto.LocationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var created = await _requests.CreateAsync(dto, HttpContext.RequestAborted);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (StockRequestException ex)
        {
            return Fail(ex.Status, ex.Message);
        }
    }

    // GET api-pos/stock-requests?locationId=&variationId=
    [HttpGet]
    [ProducesResponseType(typeof(List<StockRequestResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? locationId, [FromQuery] int? variationId)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (locationId.HasValue && PosRoleAuthorization.RequireLocationAccess(HttpContext, locationId) is { } locationDenied)
            return locationDenied;

        return Ok(await _requests.ListAsync(locationId, variationId, HttpContext.RequestAborted));
    }

    // PUT api-pos/stock-requests/{requestId}/status — IMS only (service key).
    [HttpPut("{requestId}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(string requestId, [FromBody] StockRequestDecisionDto dto)
    {
        var user = HttpContext.GetCurrentUser();
        if (user is null) return Fail(401, "The IMS service key is required.");
        if (user.Role != ServiceRole) return Fail(403, "Only IMS can approve or reject a stock request.");
        if (dto is null) return Fail(400, "Decision data is required.");

        try
        {
            var updated = await _requests.ApplyDecisionAsync(requestId, dto, HttpContext.RequestAborted);
            return Ok(new { success = true, data = new { requestId = updated.RequestId, status = updated.Status } });
        }
        catch (StockRequestException ex)
        {
            return Fail(ex.Status, ex.Message);
        }
    }

    private static ObjectResult Fail(int status, string message) =>
        new(new { success = false, message }) { StatusCode = status };
}
