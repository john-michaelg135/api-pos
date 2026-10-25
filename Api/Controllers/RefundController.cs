using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Refund;
using Api.Middlewares;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/refunds")]
public class RefundController : ControllerBase
{
    private readonly IRefundService _refundService;
    private readonly PosDbContext _db;

    public RefundController(IRefundService refundService, PosDbContext db)
    {
        _refundService = refundService;
        _db = db;
    }

    // ── US-POS-016: Submit refund request — cashier/customer files a return ──

    // POST api-pos/refunds
    [HttpPost]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "Refund request is already being submitted. Please wait a moment.")]
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitRefund([FromBody] CreateRefundRequestDto dto)
    {
        // Filing a refund request is a cashier capability → Order Management write.
        if (PosRoleAuthorization.RequireCashierOrManager(HttpContext) is { } denied) return denied;

        if (dto == null) return BadRequest("Refund data is required.");
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, dto.LocationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var refund = await _refundService.SubmitRefundAsync(dto);
            return CreatedAtAction(nameof(GetAllRefunds), new { }, refund);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // ── US-POS-017: Approve refund — manager reviews and restores stock ──

    // PUT api-pos/refunds/{id}/approve
    [HttpPut("{id:int}/approve")]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "This refund is already being approved. Please wait a moment.")]
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveRefund(int id, [FromBody] ApproveRefundDto dto)
    {
        // Approving a refund is a manager capability → Order Management approve.
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;

        if (dto == null) return BadRequest("Approval data is required.");
        var locationId = await _db.RefundRequests
            .Where(refund => refund.RefundRequestId == id)
            .Select(refund => (int?)refund.LocationId)
            .FirstOrDefaultAsync();
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, locationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var refund = await _refundService.ApproveRefundAsync(id, dto);
            if (refund == null) return NotFound($"Refund request with ID {id} not found.");
            return Ok(refund);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // PUT api-pos/refunds/{id}/reject
    [HttpPut("{id:int}/reject")]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "This refund is already being rejected. Please wait a moment.")]
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectRefund(int id, [FromBody] ApproveRefundDto dto)
    {
        // Rejecting a refund is a manager capability → Order Management approve.
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;

        if (dto == null) return BadRequest("Rejection data is required.");
        var locationId = await _db.RefundRequests
            .Where(refund => refund.RefundRequestId == id)
            .Select(refund => (int?)refund.LocationId)
            .FirstOrDefaultAsync();
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, locationId) is { } locationDenied)
            return locationDenied;

        try
        {
            var refund = await _refundService.RejectRefundAsync(id, dto);
            if (refund == null) return NotFound($"Refund request with ID {id} not found.");
            return Ok(refund);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }


    // ── Supporting: List all refund requests for manager review ──

    // GET api-pos/refunds
    [HttpGet]
    [ProducesResponseType(typeof(List<RefundResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllRefunds()
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        var refunds = await _refundService.GetAllRefundsAsync();
        var user = HttpContext.GetCurrentUser();
        if (user != null && !PosRoleAuthorization.IsAdministrator(user))
            refunds = refunds.Where(refund => user.LocationIds.Contains(refund.LocationId)).ToList();
        return Ok(refunds);
    }
}
