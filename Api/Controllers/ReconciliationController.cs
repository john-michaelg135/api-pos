using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Scms;

namespace Api.Controllers;

/// <summary>
/// POS-side reconciliation for SCM stock transfers: report drift between what SCM
/// said it shipped and what POS actually received, and heal a single transfer.
/// </summary>
[ApiController]
[Route("api-pos/reconciliation")]
public class ReconciliationController : ControllerBase
{
    private readonly IReconciliationService _reconciliationService;

    public ReconciliationController(IReconciliationService reconciliationService)
    {
        _reconciliationService = reconciliationService;
    }

    // GET api-pos/reconciliation/transfers?onlyDiscrepancies=true
    // Full reconciliation report across transfers (optionally only those with issues).
    [HttpGet("transfers")]
    [ProducesResponseType(typeof(ReconciliationReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport([FromQuery] bool onlyDiscrepancies = false)
    {
        var report = await _reconciliationService.BuildReportAsync(onlyDiscrepancies);
        return Ok(report);
    }

    // POST api-pos/reconciliation/transfers/{transferId}/resync
    // Re-match lines, recompute the verdict, and re-attempt the SCM status callback.
    [HttpPost("transfers/{transferId}/resync")]
    [ProducesResponseType(typeof(ReconciliationResyncResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResyncTransfer(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            return BadRequest("A transferId is required.");

        var result = await _reconciliationService.ResyncTransferAsync(transferId);

        // The service reports "not found" via a message rather than throwing.
        if (result.Messages.Any(m => m.Contains("not found", System.StringComparison.OrdinalIgnoreCase)))
            return NotFound(result);

        return Ok(result);
    }
}
