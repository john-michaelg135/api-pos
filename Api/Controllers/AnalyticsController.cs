using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Analytics;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsService _analyticsService;

    public AnalyticsController(IAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
    }

    // ── POS-021: Revenue summary (D/W/M) ──

    // GET api-pos/analytics/revenue?groupBy=Day|Week|Month&dateFrom=&dateTo=&locationId=&orderType=&orderSource=
    [HttpGet("revenue")]
    [ProducesResponseType(typeof(List<RevenueSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRevenueSummary([FromQuery] AnalyticsFilterDto filter)
    {
        var summary = await _analyticsService.GetRevenueSummaryAsync(filter);
        return Ok(summary);
    }

    // ── POS-022: Sales breakdowns ──

    // GET api-pos/analytics/sales/by-location
    [HttpGet("sales/by-location")]
    [ProducesResponseType(typeof(List<SalesBreakdownDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSalesByLocation([FromQuery] AnalyticsFilterDto filter)
    {
        var breakdown = await _analyticsService.GetSalesByLocationAsync(filter);
        return Ok(breakdown);
    }

    // GET api-pos/analytics/sales/by-variation
    [HttpGet("sales/by-variation")]
    [ProducesResponseType(typeof(List<SalesBreakdownDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSalesByVariation([FromQuery] AnalyticsFilterDto filter)
    {
        var breakdown = await _analyticsService.GetSalesByVariationAsync(filter);
        return Ok(breakdown);
    }

    // GET api-pos/analytics/sales/by-channel
    [HttpGet("sales/by-channel")]
    [ProducesResponseType(typeof(List<SalesBreakdownDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSalesByChannel([FromQuery] AnalyticsFilterDto filter)
    {
        var breakdown = await _analyticsService.GetSalesByChannelAsync(filter);
        return Ok(breakdown);
    }
}
