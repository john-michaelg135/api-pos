using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Scms;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/scms")]
public class ScmsIntegrationController : ControllerBase
{
    private readonly IScmsIntegrationService _scmsService;

    public ScmsIntegrationController(IScmsIntegrationService scmsService)
    {
        _scmsService = scmsService;
    }

    // ── US-POS-028: Trigger a pull from the SCMS API and update local stock ──

    // POST api-pos/scms/pull-deliveries
    [HttpPost("pull-deliveries")]
    [ProducesResponseType(typeof(ScmsPullSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> PullDeliveries()
    {
        try
        {
            var summary = await _scmsService.PullAndReceiveDeliveriesAsync();
            return Ok(summary);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Failed to pull deliveries from SCMS API.",
                detail  = ex.Message
            });
        }
    }
}
