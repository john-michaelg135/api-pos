using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Refund;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/refunds")]
public class RefundController : ControllerBase
{
    private readonly IRefundService _refundService;

    public RefundController(IRefundService refundService)
    {
        _refundService = refundService;
    }

    // ── US-POS-016: Submit refund request — cashier/customer files a return ──

    // POST api-pos/refunds
    [HttpPost]
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitRefund([FromBody] CreateRefundRequestDto dto)
    {
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
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveRefund(int id, [FromBody] ApproveRefundDto dto)
    {
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
    [ProducesResponseType(typeof(RefundResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RejectRefund(int id, [FromBody] ApproveRefundDto dto)
    {
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
