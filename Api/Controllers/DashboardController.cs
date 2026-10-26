using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Dashboard;
using Api.Middlewares;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    // ── Owner dashboard (Super Admin / CEO only) ──

    // GET api-pos/dashboard/owner?period=today|week|month&locationId=
    [HttpGet("owner")]
    [ProducesResponseType(typeof(OwnerDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOwnerDashboard([FromQuery] string? period, [FromQuery] int? locationId, CancellationToken cancellationToken)
    {
        if (PosRoleAuthorization.RequireAdministrator(HttpContext) is { } denied) return denied;
        var dashboard = await _dashboardService.GetOwnerDashboardAsync(period, locationId, cancellationToken);
        return Ok(dashboard);
    }
}
