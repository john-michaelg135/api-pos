using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Refund;
using Api.Middlewares;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/refunds")]
public class RefundController : ControllerBase
{
    private readonly IRefundService _refundService;
    private readonly bool _authEnabled;

    public RefundController(IRefundService refundService, IConfiguration configuration)
    {
        _refundService = refundService;

        // Enforce RBAC only when auth is enabled. This matches the auth pipeline:
        // when AUTH_MIDDLEWARE_ENABLED is not "true" (local dev), JwtBearer auth is
        // not wired up and requests pass through unauthenticated, so gating here
        // would break the dev flow.
        _authEnabled = string.Equals(
            configuration["AUTH_MIDDLEWARE_ENABLED"], "true",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enforces granular RBAC for refund actions. Returns null when allowed, or a
    /// 401/403 result when the caller lacks the required POS Order Management action.
    /// Mirrors the web-pos frontend gating so the API cannot be bypassed directly.
    /// No-op when auth enforcement is disabled (local dev).
    /// </summary>
    private IActionResult? RequireOrderManagement(PosAction action)
    {
        if (!_authEnabled) return null;

        var user = HttpContext.GetCurrentUser();
        if (user == null)
        {
            return Unauthorized(new { message = "Unauthorized: unable to resolve the current user." });
        }

        if (!user.Can(PosModules.OrderManagement, action))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "Forbidden: you do not have permission to perform this refund action."
            });
        }

        return null;
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
        if (RequireOrderManagement(PosAction.Write) is { } denied) return denied;

        if (dto == null) return BadRequest("Refund data is required.");

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
        if (RequireOrderManagement(PosAction.Approve) is { } denied) return denied;

        if (dto == null) return BadRequest("Approval data is required.");

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
        if (RequireOrderManagement(PosAction.Approve) is { } denied) return denied;

        if (dto == null) return BadRequest("Rejection data is required.");

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
        var refunds = await _refundService.GetAllRefundsAsync();
        return Ok(refunds);
    }
}
