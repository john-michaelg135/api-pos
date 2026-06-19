using Api.Contracts.Analytics;
using Applications.Interfaces;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class AnalyticsService : IAnalyticsService
{
    private readonly PosDbContext _db;

    public AnalyticsService(PosDbContext db)
    {
        _db = db;
    }

    // ────────────────────────────────────────────────────
    // POS-021: Revenue aggregated by Day / Week / Month
    // Only "Completed" orders are counted.
    // ────────────────────────────────────────────────────

    public async Task<List<RevenueSummaryDto>> GetRevenueSummaryAsync(AnalyticsFilterDto filter)
    {
        var query = _db.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Completed" || o.OrderStatus == "Refund Requested")
            .AsQueryable();

        query = ApplyCommonFilters(query, filter);

        var orders = await query
            .Include(o => o.OrderItems)
            .ToListAsync();

        var groupBy = filter.GroupBy?.ToLower() ?? "day";

        var grouped = orders
            .GroupBy(o => groupBy switch
            {
                "week"  => $"{o.CreatedAt:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(o.CreatedAt):D2}",
                "month" => o.CreatedAt.ToString("yyyy-MM"),
                _       => o.CreatedAt.ToString("yyyy-MM-dd")   // default: day
            })
            .OrderBy(g => g.Key)
            .Select(g => new RevenueSummaryDto
            {
                Period         = g.Key,
                TotalRevenue   = g.Sum(o => o.TotalAmount),
                TotalOrders    = g.Count(),
                TotalUnitsSold = g.SelectMany(o => o.OrderItems).Sum(i => i.Quantity)
            })
            .ToList();

        return grouped;
    }

    // ────────────────────────────────────────────────────
    // POS-022: Sales breakdown by location
    // ────────────────────────────────────────────────────

    public async Task<List<SalesBreakdownDto>> GetSalesByLocationAsync(AnalyticsFilterDto filter)
    {
        var query = _db.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Completed" || o.OrderStatus == "Refund Requested")
            .AsQueryable();

        query = ApplyCommonFilters(query, filter);

        var orders = await query
            .Include(o => o.Location)
            .Include(o => o.OrderItems)
            .ToListAsync();

        return orders
            .GroupBy(o => o.Location?.LocationName ?? "Unknown")
            .OrderByDescending(g => g.Sum(o => o.TotalAmount))
            .Select(g => new SalesBreakdownDto
            {
                Label          = g.Key,
                TotalRevenue   = g.Sum(o => o.TotalAmount),
                TotalOrders    = g.Count(),
                TotalUnitsSold = g.SelectMany(o => o.OrderItems).Sum(i => i.Quantity)
            })
            .ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-022: Sales breakdown by product variation
    // ────────────────────────────────────────────────────

    public async Task<List<SalesBreakdownDto>> GetSalesByVariationAsync(AnalyticsFilterDto filter)
    {
        var query = _db.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Completed" || o.OrderStatus == "Refund Requested")
            .AsQueryable();

        query = ApplyCommonFilters(query, filter);

        var orders = await query
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v!.Product)
            .ToListAsync();

        return orders
            .SelectMany(o => o.OrderItems)
            .GroupBy(oi => $"{oi.ProductVariation?.Product?.ProductName ?? "Unknown"} — {oi.ProductVariation?.VariationName ?? "Unknown"}")
            .OrderByDescending(g => g.Sum(oi => oi.Subtotal))
            .Select(g => new SalesBreakdownDto
            {
                Label          = g.Key,
                TotalRevenue   = g.Sum(oi => oi.Subtotal),
                TotalOrders    = g.Select(oi => oi.OrderId).Distinct().Count(),
                TotalUnitsSold = g.Sum(oi => oi.Quantity)
            })
            .ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-022: Sales breakdown by order channel (Type + Source)
    // ────────────────────────────────────────────────────

    public async Task<List<SalesBreakdownDto>> GetSalesByChannelAsync(AnalyticsFilterDto filter)
    {
        var query = _db.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Completed" || o.OrderStatus == "Refund Requested")
            .AsQueryable();

        query = ApplyCommonFilters(query, filter);

        var orders = await query
            .Include(o => o.OrderItems)
            .ToListAsync();

        return orders
            .GroupBy(o => $"{o.OrderType} ({o.OrderSource})")
            .OrderByDescending(g => g.Sum(o => o.TotalAmount))
            .Select(g => new SalesBreakdownDto
            {
                Label          = g.Key,
                TotalRevenue   = g.Sum(o => o.TotalAmount),
                TotalOrders    = g.Count(),
                TotalUnitsSold = g.SelectMany(o => o.OrderItems).Sum(i => i.Quantity)
            })
            .ToList();
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private static IQueryable<Domains.Entities.Order> ApplyCommonFilters(
        IQueryable<Domains.Entities.Order> query,
        AnalyticsFilterDto filter)
    {
        if (filter.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= filter.DateTo.Value);

        if (filter.LocationId.HasValue)
            query = query.Where(o => o.LocationId == filter.LocationId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderType))
            query = query.Where(o => o.OrderType == filter.OrderType);

        if (!string.IsNullOrWhiteSpace(filter.OrderSource))
            query = query.Where(o => o.OrderSource == filter.OrderSource);

        return query;
    }
}
