using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.StockAdjustment;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/inventory/adjustments")]
public class StockAdjustmentController : ControllerBase
{
    private readonly IStockAdjustmentService _adjustmentService;

    public StockAdjustmentController(IStockAdjustmentService adjustmentService)
    {
        _adjustmentService = adjustmentService;
    }

    // ── US-POS-019: Submit damage/loss report — status is "PendingApproval", stock unchanged ──

    // POST api-pos/inventory/adjustments
    [HttpPost]
    [ProducesResponseType(typeof(StockAdjustmentResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitAdjustment([FromBody] CreateStockAdjustmentDto dto)
    {
        if (dto == null) return BadRequest("Adjustment data is required.");

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
    [ProducesResponseType(typeof(StockAdjustmentResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApproveAdjustment(int id, [FromBody] ApproveStockAdjustmentDto dto)
    {
        if (dto == null) return BadRequest("Approval data is required.");

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
        var adjustments = await _adjustmentService.GetAllAdjustmentsAsync();
        return Ok(adjustments);
    }
}
