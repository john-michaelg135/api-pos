using Api.Contracts.Analytics;
using Applications.Interfaces;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;

namespace Applications.Services;

public class AnalyticsService : IAnalyticsService
{
    private readonly PosDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AnalyticsService(PosDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
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

        // Group by the Philippine calendar (UTC+8), not UTC, so an evening sale lands on its own day.
        var grouped = orders
            .GroupBy(o =>
            {
                var local = ToManila(o.CreatedAt);
                return groupBy switch
                {
                    "week"  => $"{System.Globalization.ISOWeek.GetYear(local)}-W{System.Globalization.ISOWeek.GetWeekOfYear(local):D2}",
                    "month" => local.ToString("yyyy-MM"),
                    _       => local.ToString("yyyy-MM-dd")   // default: day
                };
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
            .GroupBy(o => o.Location == null ? "Unknown"
                : o.Location.LocationType == "Commissary" ? $"{o.Location.LocationName} (online orders)"
                : o.Location.LocationName)
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
    // Company sales report
    // Sales are recognised on the day the order was placed; returns on the day
    // the refund was approved, so a refund of an older sale reduces this period.
    // ────────────────────────────────────────────────────

    // A sale stays a sale once refunded; the refund is reported as a return.
    private static readonly string[] SaleStatuses = { "Completed", "Refund Requested", "Refunded" };

    // Periods longer than this are trended by month instead of by day.
    private const int MaxDailyTrendDays = 62;

    // One sold or returned order line, already labelled for every breakdown.
    private sealed record SalesFact(
        DateTime At,
        string Branch,
        string Channel,
        string Payment,
        int VariationId,
        string Sku,
        string ProductName,
        string VariationName,
        int? OrderId,
        string? RefundKey,
        int Units,
        int UnitsReturned,
        decimal Gross,
        decimal Discount,
        decimal Returns);

    public async Task<SalesReportDto> GetSalesReportAsync(AnalyticsFilterDto filter)
    {
        var report = new SalesReportDto
        {
            PeriodFrom = filter.DateFrom,
            PeriodTo = filter.DateTo,
            GeneratedAt = DateTime.UtcNow,
        };

        var facts = await LoadSalesFactsAsync(filter.LocationId, filter.DateFrom, filter.DateTo);
        report.Current = Totals(facts);

        if (filter.DateFrom.HasValue)
        {
            // The previous period has the same length and ends just before this one starts.
            var length = (filter.DateTo ?? DateTime.UtcNow) - filter.DateFrom.Value;
            report.PreviousTo = filter.DateFrom.Value.AddMilliseconds(-1);
            report.PreviousFrom = report.PreviousTo.Value - length;
            report.Previous = Totals(await LoadSalesFactsAsync(filter.LocationId, report.PreviousFrom, report.PreviousTo));
        }

        var net = report.Current.NetSales;
        report.ByBranch = Lines(facts, f => f.Branch, net);
        report.ByChannel = Lines(facts, f => f.Channel, net);
        report.ByPaymentMethod = Lines(facts, f => f.Payment, net);

        report.ByProduct = facts
            .GroupBy(f => f.VariationId)
            .Select(g =>
            {
                var first = g.First();
                var gross = g.Sum(f => f.Gross);
                var discounts = g.Sum(f => f.Discount);
                var returns = g.Sum(f => f.Returns);
                var units = g.Sum(f => f.Units);
                return new SalesReportProductDto
                {
                    Sku = first.Sku,
                    ProductName = first.ProductName,
                    VariationName = first.VariationName,
                    UnitsSold = units,
                    UnitsReturned = g.Sum(f => f.UnitsReturned),
                    GrossSales = gross,
                    Discounts = discounts,
                    Returns = returns,
                    NetSales = gross - discounts - returns,
                    AverageUnitPrice = units > 0 ? Math.Round((gross - discounts) / units, 2) : 0,
                    // Sales mix: share of gross sales, which can't go negative or past 100%
                    // when a refund of an older sale lands in this period.
                    Share = SharePercent(gross, report.Current.GrossSales),
                };
            })
            .OrderByDescending(p => p.NetSales)
            .ThenBy(p => p.ProductName)
            .ToList();

        // Trend: every day (or month) of the period, including ones with no sales.
        var trendTo = filter.DateTo ?? DateTime.UtcNow;
        var daily = filter.DateFrom.HasValue && (trendTo - filter.DateFrom.Value).TotalDays <= MaxDailyTrendDays;
        report.TrendGranularity = daily ? "day" : "month";
        string PeriodKey(DateTime utc) => ToManila(utc).ToString(daily ? "yyyy-MM-dd" : "yyyy-MM");

        var trend = Lines(facts, f => PeriodKey(f.At), net).ToDictionary(l => l.Label);
        if (filter.DateFrom.HasValue)
        {
            var cursor = ToManila(filter.DateFrom.Value).Date;
            var last = ToManila(trendTo).Date;
            while (cursor <= last)
            {
                var key = cursor.ToString(daily ? "yyyy-MM-dd" : "yyyy-MM");
                trend.TryAdd(key, new SalesReportLineDto { Label = key });
                cursor = daily ? cursor.AddDays(1) : cursor.AddMonths(1);
            }
        }
        report.Trend = trend.Values.OrderBy(l => l.Label).ToList();

        return report;
    }

    private async Task<List<SalesFact>> LoadSalesFactsAsync(int? locationId, DateTime? from, DateTime? to)
    {
        var scoped = ScopeToLocations(_db.Orders.AsNoTracking(), locationId);

        var salesQuery = scoped.Where(o => SaleStatuses.Contains(o.OrderStatus));
        if (from.HasValue) salesQuery = salesQuery.Where(o => o.CreatedAt >= from.Value);
        if (to.HasValue) salesQuery = salesQuery.Where(o => o.CreatedAt <= to.Value);

        var sales = await salesQuery
            .Include(o => o.Location)
            .Include(o => o.OrderItems)
                .ThenInclude(i => i.ProductVariation)
                    .ThenInclude(v => v!.Product)
            .ToListAsync();

        // Whole-order refunds (the order was marked Refunded without per-item refund requests).
        var orderRefundQuery = scoped.Where(o => o.OrderStatus == "Refunded"
            && o.ApprovedAt.HasValue
            && !_db.RefundRequests.Any(r => r.OrderId == o.OrderId && r.Status == "Approved"));
        if (from.HasValue) orderRefundQuery = orderRefundQuery.Where(o => o.ApprovedAt >= from.Value);
        if (to.HasValue) orderRefundQuery = orderRefundQuery.Where(o => o.ApprovedAt <= to.Value);

        var orderRefunds = await orderRefundQuery
            .Include(o => o.Location)
            .Include(o => o.OrderItems)
                .ThenInclude(i => i.ProductVariation)
                    .ThenInclude(v => v!.Product)
            .ToListAsync();

        // Item refunds approved through refund requests.
        var itemRefundQuery =
            from refund in _db.RefundRequests.AsNoTracking()
            join order in scoped on refund.OrderId equals order.OrderId
            where refund.Status == "Approved" && refund.ApprovedAt.HasValue
            select new
            {
                refund.RefundRequestId,
                At = refund.ApprovedAt!.Value,
                refund.QuantityToReturn,
                UnitPrice = _db.OrderItems
                    .Where(i => i.OrderId == refund.OrderId && i.VariationId == refund.VariationId)
                    .Select(i => i.UnitPrice)
                    .FirstOrDefault(),
                order.OrderType,
                order.OrderSource,
                order.PaymentMethod,
                order.Location,
                refund.VariationId,
                Sku = refund.Variation!.Sku,
                refund.Variation.VariationName,
                refund.Variation.Product.ProductName,
            };
        if (from.HasValue) itemRefundQuery = itemRefundQuery.Where(r => r.At >= from.Value);
        if (to.HasValue) itemRefundQuery = itemRefundQuery.Where(r => r.At <= to.Value);
        var itemRefunds = await itemRefundQuery.ToListAsync();

        var facts = new List<SalesFact>();

        foreach (var order in sales)
        {
            var discounted = !string.IsNullOrWhiteSpace(order.SeniorPwdId);
            foreach (var item in order.OrderItems)
            {
                // Senior/PWD lines store the discounted price; recover the list price
                // (price ÷ 1.12 × 0.80 at checkout) so the discount shows as its own line.
                var gross = discounted
                    ? Math.Round(item.UnitPrice * 1.12m / 0.80m, 2) * item.Quantity
                    : item.Subtotal;
                facts.Add(new SalesFact(
                    order.CreatedAt, BranchLabel(order.Location), ChannelLabel(order.OrderSource, order.OrderType),
                    PaymentLabel(order.PaymentMethod), item.VariationId,
                    item.ProductVariation?.Sku ?? string.Empty,
                    item.ProductVariation?.Product?.ProductName ?? "Unknown",
                    item.ProductVariation?.VariationName ?? "Unknown",
                    order.OrderId, null, item.Quantity, 0, gross, gross - item.Subtotal, 0));
            }
        }

        foreach (var order in orderRefunds)
        {
            foreach (var item in order.OrderItems)
            {
                facts.Add(new SalesFact(
                    order.ApprovedAt!.Value, BranchLabel(order.Location), ChannelLabel(order.OrderSource, order.OrderType),
                    PaymentLabel(order.PaymentMethod), item.VariationId,
                    item.ProductVariation?.Sku ?? string.Empty,
                    item.ProductVariation?.Product?.ProductName ?? "Unknown",
                    item.ProductVariation?.VariationName ?? "Unknown",
                    null, $"order-{order.OrderId}", 0, item.Quantity, 0, 0, item.Subtotal));
            }
        }

        foreach (var refund in itemRefunds)
        {
            facts.Add(new SalesFact(
                refund.At, BranchLabel(refund.Location), ChannelLabel(refund.OrderSource, refund.OrderType),
                PaymentLabel(refund.PaymentMethod), refund.VariationId,
                refund.Sku ?? string.Empty, refund.ProductName ?? "Unknown", refund.VariationName ?? "Unknown",
                null, $"refund-{refund.RefundRequestId}", 0, refund.QuantityToReturn, 0, 0,
                refund.UnitPrice * refund.QuantityToReturn));
        }

        return facts;
    }

    private static SalesReportTotalsDto Totals(IEnumerable<SalesFact> facts) => Fill(new SalesReportTotalsDto(), facts.ToList());

    private static T Fill<T>(T totals, List<SalesFact> facts) where T : SalesReportTotalsDto
    {
        totals.GrossSales = facts.Sum(f => f.Gross);
        totals.Discounts = facts.Sum(f => f.Discount);
        totals.Returns = facts.Sum(f => f.Returns);
        totals.NetSales = totals.GrossSales - totals.Discounts - totals.Returns;
        totals.Transactions = facts.Where(f => f.OrderId.HasValue).Select(f => f.OrderId).Distinct().Count();
        totals.UnitsSold = facts.Sum(f => f.Units);
        totals.RefundCount = facts.Where(f => f.RefundKey != null).Select(f => f.RefundKey).Distinct().Count();
        totals.AverageTransaction = totals.Transactions > 0
            ? Math.Round((totals.GrossSales - totals.Discounts) / totals.Transactions, 2)
            : 0;
        return totals;
    }

    private static List<SalesReportLineDto> Lines(List<SalesFact> facts, Func<SalesFact, string> key, decimal netTotal) =>
        facts
            .GroupBy(key)
            .Select(g =>
            {
                var line = Fill(new SalesReportLineDto { Label = g.Key }, g.ToList());
                line.Share = SharePercent(line.NetSales, netTotal);
                return line;
            })
            .OrderByDescending(l => l.NetSales)
            .ThenBy(l => l.Label)
            .ToList();

    private static decimal SharePercent(decimal part, decimal total) =>
        total != 0 ? Math.Round(part / total * 100, 1) : 0;

    private static string BranchLabel(Domains.Entities.Location? location) =>
        location == null ? "Unassigned"
        : location.LocationType == "Commissary" ? $"{location.LocationName} (online orders)"
        : location.LocationName;

    // POS channels are Store/Walk-in, Bazaar and Institutional; Online only appears for
    // older e-commerce orders, if any are in the period.
    private static string ChannelLabel(string? orderSource, string? orderType) =>
        orderSource == "Ecommerce" || orderType == "Online" ? "Online"
        : orderType == "Institutional" ? "Institutional"
        : orderType == "Bazaar" ? "Bazaar"
        : "Store/Walk-in";

    private static string PaymentLabel(string? method) => method switch
    {
        "BankTransfer" => "Bank transfer",
        "COD" => "Cash on delivery",
        null or "" => "Unspecified",
        _ => method,
    };

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private static DateTime ToManila(DateTime utc) => utc.AddHours(8);

    // Limits orders to the caller's branches (super users see all), then to the requested branch.
    private IQueryable<Domains.Entities.Order> ScopeToLocations(
        IQueryable<Domains.Entities.Order> query,
        int? locationId)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();

        if (currentUser is { IsSuperUser: false })
        {
            var allowedLocations = currentUser.LocationIds;
            if (allowedLocations.Length == 0 ||
                (locationId.HasValue && !allowedLocations.Contains(locationId.Value)))
            {
                return query.Where(_ => false);
            }

            query = query.Where(o =>
                o.LocationId.HasValue && allowedLocations.Contains(o.LocationId.Value));
        }

        if (locationId.HasValue)
            query = query.Where(o => o.LocationId == locationId.Value);

        return query;
    }

    private IQueryable<Domains.Entities.Order> ApplyCommonFilters(
        IQueryable<Domains.Entities.Order> query,
        AnalyticsFilterDto filter)
    {
        query = ScopeToLocations(query, filter.LocationId);

        if (filter.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= filter.DateTo.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderType))
            query = query.Where(o => o.OrderType == filter.OrderType);

        if (!string.IsNullOrWhiteSpace(filter.OrderSource))
            query = query.Where(o => o.OrderSource == filter.OrderSource);

        return query;
    }
}
