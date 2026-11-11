using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Applications.Interfaces;
using Api.Contracts.Dashboard;
using Api.Middlewares;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/dashboard")]
public class DashboardController : ControllerBase
{
    // The automatic refresh reuses a result this recent instead of re-running the queries.
    private static readonly TimeSpan OwnerDashboardCacheFor = TimeSpan.FromSeconds(60);

    private readonly IDashboardService _dashboardService;
    private readonly IMemoryCache _cache;

    public DashboardController(IDashboardService dashboardService, IMemoryCache cache)
    {
        _dashboardService = dashboardService;
        _cache = cache;
    }

    // ── Owner dashboard (Super Admin / CEO only) ──

    // GET api-pos/dashboard/owner?period=today|week|month&locationId=&fresh=true
    // fresh=true skips the cache; the dashboard's Refresh button sends it.
    [HttpGet("owner")]
    [ProducesResponseType(typeof(OwnerDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOwnerDashboard(
        [FromQuery] string? period,
        [FromQuery] int? locationId,
        [FromQuery] bool fresh,
        CancellationToken cancellationToken)
    {
        if (PosRoleAuthorization.RequireAdministrator(HttpContext) is { } denied) return denied;

        var normalizedPeriod = period?.Trim().ToLowerInvariant() switch
        {
            "week" => "week",
            "month" => "month",
            _ => "today",
        };
        // Include the Philippine date so a cached "today" never outlives midnight.
        var manilaDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var cacheKey = $"owner-dashboard:{normalizedPeriod}:{locationId?.ToString() ?? "all"}:{manilaDate:yyyy-MM-dd}";

        if (!fresh && _cache.TryGetValue(cacheKey, out OwnerDashboardDto? cached) && cached is not null)
            return Ok(cached);

        var dashboard = await _dashboardService.GetOwnerDashboardAsync(normalizedPeriod, locationId, cancellationToken);
        _cache.Set(cacheKey, dashboard, OwnerDashboardCacheFor);
        return Ok(dashboard);
    }
}
