using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.StockAdjustment;
using Api.Middlewares;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/inventory/adjustments")]
public class StockAdjustmentController : ControllerBase
{
    private readonly IStockAdjustmentService _adjustmentService;
    private readonly PosDbContext _db;

    public StockAdjustmentController(IStockAdjustmentService adjustmentService, PosDbContext db)
    {
        _adjustmentService = adjustmentService;
        _db = db;
    }

    // ── US-POS-019: Submit damage/loss report — status is "PendingApproval", stock unchanged ──

    // POST api-pos/inventory/adjustments
    [HttpPost]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "Adjustment is already being submitted. Please wait a moment.")]
    [ProducesResponseType(typeof(StockAdjustmentResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitAdjustment([FromBody] CreateStockAdjustmentDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Adjustment data is required.");
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, dto.LocationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var adjustment = await _adjustmentService.SubmitAdjustmentAsync(dto);
            return CreatedAtAction(nameof(GetAllAdjustments), new { }, adjustment);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── US-POS-020: Approve adjustment — executes stock deduction, logs manager ──

    // PUT api-pos/inventory/adjustments/{id}/approve
    [HttpPut("{id:int}/approve")]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "This adjustment is already being approved. Please wait a moment.")]
    [ProducesResponseType(typeof(StockAdjustmentResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveAdjustment(int id, [FromBody] ApproveStockAdjustmentDto dto)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        if (dto == null) return BadRequest("Approval data is required.");
        var locationId = await _db.StockAdjustments
            .Where(adjustment => adjustment.AdjustmentId == id)
            .Select(adjustment => (int?)adjustment.LocationId)
            .FirstOrDefaultAsync();
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, locationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var adjustment = await _adjustmentService.ApproveAdjustmentAsync(id, dto);
            if (adjustment == null) return NotFound($"Stock adjustment with ID {id} not found.");
            return Ok(adjustment);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── Supporting: List all adjustments for manager review ──

    // GET api-pos/inventory/adjustments
    [HttpGet]
    [ProducesResponseType(typeof(List<StockAdjustmentResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllAdjustments()
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        var adjustments = await _adjustmentService.GetAllAdjustmentsAsync();
        var user = HttpContext.GetCurrentUser();
        if (user != null && !PosRoleAuthorization.IsAdministrator(user))
            adjustments = adjustments.Where(item => user.LocationIds.Contains(item.LocationId)).ToList();
        return Ok(adjustments);
    }
}
